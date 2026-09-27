using Coppertop.Trader.Api;
using Coppertop.Trader.Kraken;
using Microsoft.Extensions.Options;

namespace Coppertop.Trader.Trading;

/// <summary>
/// One deterministic trading cycle: no LLM calls, no tokens.
/// Paper mode: entries are optionally checked with Kraken (validate=true, nothing is placed)
/// and fills are simulated at the limit price.
/// </summary>
public sealed class TraderEngine
{
    private readonly CoppertopApiClient _api;
    private readonly KrakenClient _kraken;
    private readonly TraderOptions _o;
    private readonly CandleCache _candles;
    private readonly TimeProvider _clock;
    private readonly ILogger<TraderEngine> _log;

    public TraderEngine(
        CoppertopApiClient api,
        KrakenClient kraken,
        IOptions<TraderOptions> options,
        CandleCache candles,
        TimeProvider clock,
        ILogger<TraderEngine> log)
    {
        _api = api;
        _kraken = kraken;
        _o = options.Value;
        _candles = candles;
        _clock = clock;
        _log = log;
    }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        IReadOnlyList<OpportunityDto> opportunities;
        IReadOnlyList<PositionDto> positions;
        SummaryDto summary;
        try
        {
            opportunities = await _api.GetActiveOpportunitiesAsync(ct);
            positions = await _api.GetOpenPositionsAsync(ct);
            summary = await _api.GetSummaryAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            // Without the API we can't record anything, so we don't trade. Live exits would already sit on Kraken.
            _log.LogWarning("Coppertop API unreachable, skipping cycle: {Message}", ex.Message);
            return;
        }

        var assets = opportunities.Select(x => x.Asset).Concat(positions.Select(p => p.Asset))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (assets.Count == 0)
        {
            _log.LogInformation("No opportunities or open positions.");
            return;
        }

        var pairs = await _kraken.EnsurePairsAsync(assets, ct);
        var tickers = await _kraken.GetTickersAsync(assets, ct);

        var closedThisCycle = await ManageExitsAsync(positions, tickers, ct);

        var stillOpen = positions.Where(p => !closedThisCycle.Contains(p.Id)).ToList();
        var cash = _o.PaperStartingCashUsd + summary.RealizedPnlUsd - summary.OpenExposureUsd;
        var exposure = stillOpen.GroupBy(p => p.Asset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.CostUsd), StringComparer.OrdinalIgnoreCase);
        var portfolio = new PortfolioState(cash, stillOpen.Count, exposure);

        foreach (var opp in opportunities.OrderByDescending(x => x.Confidence))
        {
            if (exposure.ContainsKey(opp.Asset))
            {
                _log.LogDebug("{Asset}: already holding, skip", opp.Asset);
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

            if (await EnterAsync(opp, pair, decision.Plan, ct))
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

    private async Task<HashSet<long>> ManageExitsAsync(
        IReadOnlyList<PositionDto> positions, IReadOnlyDictionary<string, Ticker> tickers, CancellationToken ct)
    {
        var closed = new HashSet<long>();
        foreach (var position in positions)
        {
            if (!tickers.TryGetValue(position.Asset, out var ticker)) continue;
            var reason = DipStrategy.ExitReason(position, ticker);
            if (reason is null) continue;

            // TP is a resting maker limit at the TP price; SL is a taker market sell at the bid.
            var (price, feePct, orderType) = reason == "take_profit"
                ? (position.TakeProfitPrice, _o.MakerFeePct, "limit")
                : (ticker.Bid, _o.TakerFeePct, "market");
            var fee = FeeMath.Fee(price * position.Volume, feePct);

            var order = await _api.CreateOrderAsync(new CreateOrderDto(
                position.OpportunityId, position.Id, position.Asset, "sell", orderType, reason,
                price, position.Volume, "simulated", null, true, "paper fill"), ct);
            await _api.CreateTradeAsync(new CreateTradeDto(
                order.Id, position.Id, position.Asset, "sell", price, position.Volume, fee, true, _clock.GetUtcNow()), ct);
            await _api.ClosePositionAsync(position.Id, new ClosePositionDto(price, fee, reason), ct);

            closed.Add(position.Id);
            _log.LogInformation("{Asset}: closed position {Id} via {Reason} at {Price} (fee {Fee:F4})",
                position.Asset, position.Id, reason, price, fee);
        }
        return closed;
    }

    private async Task<bool> EnterAsync(OpportunityDto opp, PairInfo pair, EntryPlan plan, CancellationToken ct)
    {
        if (!await _api.TryConsumeOpportunityAsync(opp.Id, ct))
        {
            _log.LogInformation("{Asset}: opportunity {Id} no longer active", opp.Asset, opp.Id);
            return false;
        }

        var status = "simulated";
        string? note = "paper fill (no Kraken credentials, not validated)";
        if (_kraken.HasCredentials)
        {
            try
            {
                var validated = await _kraken.AddOrderAsync(new AddOrderRequest(
                    pair.AltName, "buy", "limit", plan.EntryPrice, plan.Volume, PostOnly: true,
                    CloseLimitPrice: plan.TakeProfitPrice, ValidateOnly: true), pair, ct);
                status = "validated";
                note = $"kraken validate ok: {validated.Description} | close: {validated.CloseDescription}";
            }
            catch (KrakenException ex)
            {
                _log.LogWarning("{Asset}: Kraken rejected order during validation: {Message}", opp.Asset, ex.Message);
                await _api.CreateOrderAsync(new CreateOrderDto(
                    opp.Id, null, opp.Asset, "buy", "limit", "entry", plan.EntryPrice, plan.Volume,
                    "rejected", null, true, ex.Message), ct);
                return false;
            }
        }

        var fee = FeeMath.Fee(plan.SpendUsd, _o.MakerFeePct);
        var position = await _api.OpenPositionAsync(new OpenPositionDto(
            opp.Id, opp.Asset, plan.Volume, plan.EntryPrice, fee, plan.TakeProfitPrice, plan.StopLossPrice, true), ct);
        var order = await _api.CreateOrderAsync(new CreateOrderDto(
            opp.Id, position.Id, opp.Asset, "buy", "limit", "entry", plan.EntryPrice, plan.Volume,
            status, null, true, note), ct);
        await _api.CreateTradeAsync(new CreateTradeDto(
            order.Id, position.Id, opp.Asset, "buy", plan.EntryPrice, plan.Volume, fee, true, _clock.GetUtcNow()), ct);

        _log.LogInformation("{Asset}: ENTER {Volume} @ {Price} (${Spend:F2}) – {Rationale}",
            opp.Asset, plan.Volume, plan.EntryPrice, plan.SpendUsd, plan.Rationale);
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
