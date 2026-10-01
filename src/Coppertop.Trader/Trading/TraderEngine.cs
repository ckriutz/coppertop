using Coppertop.Trader.Api;
using Coppertop.Trader.Kraken;
using Microsoft.Extensions.Options;

namespace Coppertop.Trader.Trading;

/// <summary>
/// One deterministic trading cycle: no LLM calls, no tokens.
/// Paper mode: entries rest as "open" orders and only fill when Kraken's public trades print through
/// the limit (see <see cref="PaperFills"/>). Entries are optionally checked with Kraken (validate=true, nothing is placed).
/// </summary>
public sealed class TraderEngine
{
    private readonly CoppertopApiClient _api;
    private readonly KrakenClient _kraken;
    private readonly TraderOptions _o;
    private readonly CandleCache _candles;
    private readonly TradeTape _tape;
    private readonly TimeProvider _clock;
    private readonly ILogger<TraderEngine> _log;

    public TraderEngine(
        CoppertopApiClient api,
        KrakenClient kraken,
        IOptions<TraderOptions> options,
        CandleCache candles,
        TradeTape tape,
        TimeProvider clock,
        ILogger<TraderEngine> log)
    {
        _api = api;
        _kraken = kraken;
        _o = options.Value;
        _candles = candles;
        _tape = tape;
        _clock = clock;
        _log = log;
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        IReadOnlyList<OpportunityDto> opportunities;
        IReadOnlyList<PositionDto> positions;
        IReadOnlyList<OrderDto> openOrders;
        IReadOnlyList<VetoDto> vetoes;
        SummaryDto summary;
        ControlDto control;
        try
        {
            control = await _api.GetControlAsync(ct);
            opportunities = await _api.GetActiveOpportunitiesAsync(ct);
            positions = await _api.GetOpenPositionsAsync(ct);
            openOrders = await _api.GetOpenOrdersAsync(ct);
            vetoes = await _api.GetActiveVetoesAsync(ct);
            summary = await _api.GetSummaryAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            // Without the API we can't record anything, so we don't trade. Live exits would already sit on Kraken.
            _log.LogWarning("Coppertop API unreachable, skipping cycle: {Message}", ex.Message);
            return;
        }

        var assets = opportunities.Select(x => x.Asset)
            .Concat(positions.Select(p => p.Asset))
            .Concat(openOrders.Select(o => o.Asset))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (assets.Count == 0)
        {
            _log.LogInformation("No opportunities, open orders or open positions.");
            _tape.Retain([]);
            if (control.FlattenRequested) await _api.AckFlattenAsync(ct);
            await HeartbeatAsync(new Dictionary<string, Ticker>(), ct);
            return;
        }

        var pairs = await _kraken.EnsurePairsAsync(assets, ct);
        var tickers = await _kraken.GetTickersAsync(assets, ct);
        await HeartbeatAsync(tickers, ct);

        if (control.FlattenRequested)
        {
            await FlattenAsync(positions, openOrders, tickers, pairs, ct);
            return;
        }

        var trades = await LoadTradesAsync(positions, openOrders, ct);
        var vetoed = vetoes.Select(v => v.Asset).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var (filled, pending) = await ProcessOpenOrdersAsync(openOrders, tickers, trades, pairs, vetoed, control.EntriesPaused, ct);

        // Positions filled this cycle are checked against the same trade batch (only prints after their fill count).
        var closedThisCycle = await ManageExitsAsync(positions.Concat(filled).ToList(), tickers, trades, pairs, ct);

        if (control.EntriesPaused)
        {
            _log.LogInformation("Entries paused from the dashboard; managing exits only.");
            return;
        }

        var stillOpen = positions.Concat(filled).Where(p => !closedThisCycle.Contains(p.Id)).ToList();
        // Pending buys reserve cash and count as positions so we can't over-commit while waiting for fills.
        // Summary was read before this cycle's fills/exits, so newly filled costs are subtracted and exit proceeds are ignored (conservative).
        var cash = _o.PaperStartingCashUsd + summary.RealizedPnlUsd - summary.OpenExposureUsd
                   - filled.Sum(p => p.CostUsd) - pending.Sum(Reserved);
        var exposure = stillOpen.Select(p => (p.Asset, Usd: p.CostUsd))
            .Concat(pending.Select(o => (o.Asset, Usd: Reserved(o))))
            .GroupBy(x => x.Asset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Usd), StringComparer.OrdinalIgnoreCase);
        var portfolio = new PortfolioState(cash, stillOpen.Count + pending.Count, exposure);

        foreach (var opp in opportunities.OrderByDescending(x => x.Confidence))
        {
            if (exposure.ContainsKey(opp.Asset))
            {
                _log.LogDebug("{Asset}: already holding or waiting on an order, skip", opp.Asset);
                continue;
            }
            if (!tickers.TryGetValue(opp.Asset, out var ticker) || !pairs.TryGetValue(opp.Asset, out var pair))
            {
                _log.LogWarning("{Asset}: no Kraken ticker/pair info, skip", opp.Asset);
                continue;
            }

            var closes = await _candles.GetClosesAsync(_kraken, opp.Asset, _o.CandleIntervalMinutes, ct);
            var decision = DipStrategy.Evaluate(opp, ticker, closes, pair, portfolio, _o);
            if (decision.Plan is null)
            {
                _log.LogInformation("{Asset}: no entry – {Reason}", opp.Asset, decision.Reason);
                continue;
            }

            var context = EntryContext.Build(opp, ticker, closes, decision.Plan, portfolio, _clock.GetUtcNow(), _o);
            if (await PlaceEntryAsync(opp, pair, decision.Plan, context, ct))
            {
                var cost = decision.Plan.SpendUsd + FeeMath.Fee(decision.Plan.SpendUsd, _o.MakerFeePct);
                exposure[opp.Asset] = cost;
                portfolio = portfolio with
                {
                    AvailableCashUsd = portfolio.AvailableCashUsd - cost,
                    OpenPositions = portfolio.OpenPositions + 1,
                    ExposureByAsset = exposure
                };
            }
        }
    }

    private decimal Reserved(OrderDto order) =>
        order.Price * order.Volume + FeeMath.Fee(order.Price * order.Volume, _o.MakerFeePct);

    private async Task<Dictionary<string, IReadOnlyList<PublicTrade>>> LoadTradesAsync(
        IReadOnlyList<PositionDto> positions, IReadOnlyList<OrderDto> openOrders, CancellationToken ct)
    {
        var earliest = positions.Select(p => (p.Asset, At: p.OpenedAt))
            .Concat(openOrders.Select(o => (o.Asset, At: o.CreatedAt)))
            .GroupBy(x => x.Asset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Min(x => x.At), StringComparer.OrdinalIgnoreCase);
        _tape.Retain(earliest.Keys);

        var result = new Dictionary<string, IReadOnlyList<PublicTrade>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (asset, at) in earliest)
        {
            try
            {
                result[asset] = await _tape.GetNewTradesAsync(_kraken, asset, at, _o, ct);
            }
            catch (Exception ex) when (ex is KrakenException or HttpRequestException)
            {
                // Fall back to ticker-only checks for this asset; the cursor is unchanged so we retry next cycle.
                _log.LogWarning("{Asset}: couldn't load Kraken trades, using ticker only: {Message}", asset, ex.Message);
                result[asset] = [];
            }
        }
        return result;
    }

    private async Task<(List<PositionDto> Filled, List<OrderDto> Pending)> ProcessOpenOrdersAsync(
        IReadOnlyList<OrderDto> openOrders,
        IReadOnlyDictionary<string, Ticker> tickers,
        IReadOnlyDictionary<string, IReadOnlyList<PublicTrade>> trades,
        IReadOnlyDictionary<string, PairInfo> pairs,
        HashSet<string> vetoed,
        bool entriesPaused,
        CancellationToken ct)
    {
        var filled = new List<PositionDto>();
        var pending = new List<OrderDto>();
        var now = _clock.GetUtcNow();

        foreach (var order in openOrders)
        {
            var ticker = tickers.GetValueOrDefault(order.Asset);

            if (!order.IsSimulated || order.Side != "buy" || order.Purpose != "entry")
            {
                // Paper exits fill in the same cycle they're created; anything left over is a stray from a crashed cycle.
                if (order.IsSimulated && now - order.CreatedAt >= TimeSpan.FromMinutes(_o.EntryOrderTimeoutMinutes))
                    await CancelAsync(order, "stale paper order", ct);
                continue;
            }

            var fill = PaperFills.Entry(order, trades.GetValueOrDefault(order.Asset) ?? [], ticker, now);
            if (fill is not null)
            {
                var position = await FillEntryAsync(order, fill, pairs, ct);
                if (position is not null) filled.Add(position);
                continue;
            }

            var cancelReason = PaperFills.EntryCancelReason(order, ticker, vetoed.Contains(order.Asset), entriesPaused, now, _o);
            if (cancelReason is not null)
            {
                await CancelAsync(order, cancelReason, ct);
                continue;
            }

            pending.Add(order);
        }
        return (filled, pending);
    }

    private async Task<PositionDto?> FillEntryAsync(OrderDto order, PaperFill fill, IReadOnlyDictionary<string, PairInfo> pairs, CancellationToken ct)
    {
        var opp = order.OpportunityId is { } oppId ? await _api.GetOpportunityAsync(oppId, ct) : null;
        if (opp is null || !pairs.TryGetValue(order.Asset, out var pair))
        {
            await CancelAsync(order, "can't determine exits (missing opportunity or pair)", ct);
            return null;
        }

        var (tp, sl) = DipStrategy.ExitPrices(fill.Price, opp.TakeProfitPct, opp.StopLossPct, pair.PriceDecimals);
        var fee = FeeMath.Fee(fill.Price * order.Volume, _o.MakerFeePct);
        var result = await _api.TryFillOrderAsync(order.Id, new FillOrderDto(
            fill.Price, order.Volume, fee, fill.ExecutedAt, tp, sl, $"{order.Note} | paper fill: {fill.Evidence}"), ct);
        if (result is null)
        {
            _log.LogInformation("{Asset}: order {Id} was no longer open", order.Asset, order.Id);
            return null;
        }

        _log.LogInformation("{Asset}: FILLED entry order {Id}: {Volume} @ {Price} ({Evidence}); tp {Tp} sl {Sl}",
            order.Asset, order.Id, order.Volume, fill.Price, fill.Evidence, tp, sl);
        return result.Position;
    }

    private async Task CancelAsync(OrderDto order, string reason, CancellationToken ct)
    {
        if (await _api.TryCancelOrderAsync(order.Id, $"{order.Note} | cancelled: {reason}", ct))
            _log.LogInformation("{Asset}: cancelled order {Id} – {Reason}", order.Asset, order.Id, reason);
    }

    private async Task<HashSet<long>> ManageExitsAsync(
        IReadOnlyList<PositionDto> positions,
        IReadOnlyDictionary<string, Ticker> tickers,
        IReadOnlyDictionary<string, IReadOnlyList<PublicTrade>> trades,
        IReadOnlyDictionary<string, PairInfo> pairs,
        CancellationToken ct)
    {
        var closed = new HashSet<long>();
        var now = _clock.GetUtcNow();
        foreach (var position in positions)
        {
            var ticker = tickers.GetValueOrDefault(position.Asset);
            var decimals = pairs.TryGetValue(position.Asset, out var pair) ? pair.PriceDecimals : 8;
            var fill = PaperFills.Exit(position, trades.GetValueOrDefault(position.Asset) ?? [], ticker, now, _o.StopLossSlippagePct, decimals);
            if (fill is null) continue;

            // TP is a resting maker limit; SL is a taker market sell.
            if (await CloseAsync(position, fill, ct)) closed.Add(position.Id);
        }
        return closed;
    }

    private async Task<bool> CloseAsync(PositionDto position, PaperFill fill, CancellationToken ct)
    {
        var (feePct, orderType) = fill.Reason == "take_profit" ? (_o.MakerFeePct, "limit") : (_o.TakerFeePct, "market");
        var fee = FeeMath.Fee(fill.Price * position.Volume, feePct);

        var order = await _api.CreateOrderAsync(new CreateOrderDto(
            position.OpportunityId, position.Id, position.Asset, "sell", orderType, fill.Reason,
            fill.Price, position.Volume, "open", null, true, null), ct);
        var result = await _api.TryFillOrderAsync(order.Id, new FillOrderDto(
            fill.Price, position.Volume, fee, fill.ExecutedAt, null, null, $"paper fill: {fill.Evidence}"), ct);
        if (result is null)
        {
            await _api.TryCancelOrderAsync(order.Id, "position already closed", ct);
            return false;
        }

        _log.LogInformation("{Asset}: closed position {Id} via {Reason} at {Price} ({Evidence}, fee {Fee:F4})",
            position.Asset, position.Id, fill.Reason, fill.Price, fill.Evidence, fee);
        return true;
    }

    /// <summary>Kill switch: cancel every resting order and market-sell every position, then acknowledge.</summary>
    private async Task FlattenAsync(
        IReadOnlyList<PositionDto> positions,
        IReadOnlyList<OrderDto> openOrders,
        IReadOnlyDictionary<string, Ticker> tickers,
        IReadOnlyDictionary<string, PairInfo> pairs,
        CancellationToken ct)
    {
        _log.LogWarning("FLATTEN requested: cancelling {Orders} order(s) and closing {Positions} position(s)",
            openOrders.Count, positions.Count);
        foreach (var order in openOrders) await CancelAsync(order, "flatten", ct);

        var leftOver = 0;
        var now = _clock.GetUtcNow();
        foreach (var position in positions)
        {
            if (!tickers.TryGetValue(position.Asset, out var ticker))
            {
                _log.LogWarning("{Asset}: no price, can't flatten position {Id} yet", position.Asset, position.Id);
                leftOver++;
                continue;
            }
            var decimals = pairs.TryGetValue(position.Asset, out var pair) ? pair.PriceDecimals : 8;
            await CloseAsync(position, PaperFlatten.Exit(ticker, now, _o.StopLossSlippagePct, decimals), ct);
        }

        // Only acknowledge once everything is out; otherwise retry next cycle.
        if (leftOver == 0) await _api.AckFlattenAsync(ct);
    }

    private async Task HeartbeatAsync(IReadOnlyDictionary<string, Ticker> tickers, CancellationToken ct)
    {
        try
        {
            var marks = tickers.Values.Select(t => new MarkDto(t.AltName, t.Bid, t.Ask, t.Last)).ToList();
            await _api.SendHeartbeatAsync(new HeartbeatDto(_o.Mode, _o.PaperStartingCashUsd, _o.CycleSeconds, marks), ct);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning("Couldn't send heartbeat/marks: {Message}", ex.Message);
        }
    }

    private async Task<bool> PlaceEntryAsync(OpportunityDto opp, PairInfo pair, EntryPlan plan, object context, CancellationToken ct)
    {
        if (!await _api.TryConsumeOpportunityAsync(opp.Id, ct))
        {
            _log.LogInformation("{Asset}: opportunity {Id} no longer active", opp.Asset, opp.Id);
            return false;
        }

        var note = "paper order (no Kraken credentials, not validated)";
        if (_kraken.HasCredentials)
        {
            try
            {
                var validated = await _kraken.AddOrderAsync(new AddOrderRequest(
                    pair.AltName, "buy", "limit", plan.EntryPrice, plan.Volume, PostOnly: true,
                    CloseLimitPrice: plan.TakeProfitPrice, ValidateOnly: true), pair, ct);
                note = $"kraken validate ok: {validated.Description} | close: {validated.CloseDescription}";
            }
            catch (KrakenException ex)
            {
                _log.LogWarning("{Asset}: Kraken rejected order during validation: {Message}", opp.Asset, ex.Message);
                await _api.CreateOrderAsync(new CreateOrderDto(
                    opp.Id, null, opp.Asset, "buy", "limit", "entry", plan.EntryPrice, plan.Volume,
                    "rejected", null, true, ex.Message, context), ct);
                return false;
            }
        }

        // No position yet: the order rests until the market trades through it (or it times out).
        var order = await _api.CreateOrderAsync(new CreateOrderDto(
            opp.Id, null, opp.Asset, "buy", "limit", "entry", plan.EntryPrice, plan.Volume,
            "open", null, true, note, context), ct);

        _log.LogInformation("{Asset}: PLACED paper buy {Id}: {Volume} @ {Price} (${Spend:F2}) – {Rationale}",
            opp.Asset, order.Id, plan.Volume, plan.EntryPrice, plan.SpendUsd, plan.Rationale);
        return true;
    }
}

public sealed class CandleCache(TimeProvider clock)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<decimal> Closes)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<decimal>> GetClosesAsync(KrakenClient kraken, string asset, int intervalMinutes, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(asset, out var hit) && now - hit.At < Ttl) return hit.Closes;

        var candles = await kraken.GetCandlesAsync(asset, intervalMinutes, ct);
        // Kraken's last candle is still forming; only use closed candles.
        var closes = candles.Take(Math.Max(0, candles.Count - 1)).Select(c => c.Close).ToList();
        _cache[asset] = (now, closes);
        return closes;
    }
}
