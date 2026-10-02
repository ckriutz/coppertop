using Coppertop.Trader.Api;
using Coppertop.Trader.Kraken;

namespace Coppertop.Trader.Trading;

public sealed record PaperFill(string Reason, decimal Price, DateTimeOffset ExecutedAt, string Evidence);

/// <summary>
/// Pessimistic paper fill model. Pure functions, no I/O.
/// A resting limit only fills when the market trades *through* its price: we don't know our place
/// in the queue, so a print exactly at our price doesn't count.
/// </summary>
public static class PaperFills
{
    /// <summary>Buy limit fills when a trade prints strictly below it after placement, or the ask drops below it.</summary>
    public static PaperFill? Entry(OrderDto order, IReadOnlyList<PublicTrade> trades, Ticker? ticker, DateTimeOffset now)
    {
        var hit = trades.Where(t => t.Time > order.CreatedAt && t.Price < order.Price).MinBy(t => t.Time);
        if (hit is not null)
            return new PaperFill("entry", order.Price, hit.Time, $"trade {hit.Price} < limit {order.Price}");
        if (ticker is not null && ticker.Ask < order.Price)
            return new PaperFill("entry", order.Price, now, $"ask {ticker.Ask} < limit {order.Price}");
        return null;
    }

    /// <summary>Why a still-unfilled entry order should be cancelled, or null to keep waiting.</summary>
    public static string? EntryCancelReason(OrderDto order, Ticker? ticker, bool vetoed, bool entriesPaused, DateTimeOffset now, TraderOptions o)
    {
        if (entriesPaused) return "entries paused";
        if (vetoed) return "asset vetoed";
        if (now - order.CreatedAt >= TimeSpan.FromMinutes(o.EntryOrderTimeoutMinutes))
            return $"not filled within {o.EntryOrderTimeoutMinutes}m";
        if (ticker is not null && ticker.Bid > order.Price * (1m + o.EntryOrderRunawayPct / 100m))
            return $"price ran away (bid {ticker.Bid} > limit {order.Price} +{o.EntryOrderRunawayPct}%)";
        return null;
    }

    /// <summary>
    /// Take-profit is a resting maker sell: fills at the TP price when a trade prints strictly above it (or bid crosses it).
    /// Stop-loss is a taker market sell once price touches the stop: fills at min(stop, bid) less slippage.
    /// Whichever happened first wins; ties go to the stop-loss.
    /// </summary>
    public static PaperFill? Exit(
        PositionDto position, IReadOnlyList<PublicTrade> trades, Ticker? ticker, DateTimeOffset now,
        decimal slippagePct, int priceDecimals)
    {
        var after = trades.Where(t => t.Time > position.OpenedAt).ToList();

        var stopTrade = after.Where(t => t.Price <= position.StopLossPrice).MinBy(t => t.Time);
        var stopAt = stopTrade?.Time ?? (ticker is not null && ticker.Bid <= position.StopLossPrice ? now : (DateTimeOffset?)null);

        var tpTrade = after.Where(t => t.Price > position.TakeProfitPrice).MinBy(t => t.Time);
        var tpAt = tpTrade?.Time ?? (ticker is not null && ticker.Bid > position.TakeProfitPrice ? now : (DateTimeOffset?)null);

        if (stopAt is { } s && (tpAt is null || s <= tpAt))
        {
            var basis = ticker is null ? position.StopLossPrice : Math.Min(position.StopLossPrice, ticker.Bid);
            var price = Math.Round(basis * (1m - slippagePct / 100m), priceDecimals, MidpointRounding.ToZero);
            var evidence = stopTrade is not null
                ? $"trade {stopTrade.Price} <= stop {position.StopLossPrice}"
                : $"bid {ticker!.Bid} <= stop {position.StopLossPrice}";
            return new PaperFill("stop_loss", price, s, evidence);
        }

        if (tpAt is { } t)
        {
            var evidence = tpTrade is not null
                ? $"trade {tpTrade.Price} > tp {position.TakeProfitPrice}"
                : $"bid {ticker!.Bid} > tp {position.TakeProfitPrice}";
            return new PaperFill("take_profit", position.TakeProfitPrice, t, evidence);
        }

        return null;
    }
}

/// <summary>Time stop: a position held MaxHoldMinutes without hitting TP or SL is market-sold at the bid less slippage.</summary>
public static class PaperTimeStop
{
    public static PaperFill? Exit(PositionDto position, Ticker? ticker, DateTimeOffset now, int? maxHoldMinutes, decimal slippagePct, int priceDecimals)
    {
        if (maxHoldMinutes is not > 0 || ticker is null) return null;
        var held = now - position.OpenedAt;
        if (held < TimeSpan.FromMinutes(maxHoldMinutes.Value)) return null;
        var price = Math.Round(ticker.Bid * (1m - slippagePct / 100m), priceDecimals, MidpointRounding.ToZero);
        return new PaperFill("time_stop", price, now, $"held {held.TotalMinutes:F0} min >= {maxHoldMinutes} min, bid {ticker.Bid}");
    }
}

/// <summary>Kill-switch exit: taker market sell at the bid less slippage.</summary>
public static class PaperFlatten
{
    public static PaperFill Exit(Ticker ticker, DateTimeOffset now, decimal slippagePct, int priceDecimals) =>
        new("flatten", Math.Round(ticker.Bid * (1m - slippagePct / 100m), priceDecimals, MidpointRounding.ToZero), now,
            $"flatten at bid {ticker.Bid}");
}

/// <summary>
/// Remembers a Kraken Trades cursor per asset so each cycle only fetches new prints.
/// That catches brief wicks between 30s cycles that the ticker snapshot would miss.
/// </summary>
public sealed class TradeTape(TimeProvider clock, ILogger<TradeTape> log)
{
    private readonly Dictionary<string, string> _cursors = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<PublicTrade>> GetNewTradesAsync(
        KrakenClient kraken, string asset, DateTimeOffset earliestNeeded, TraderOptions o, CancellationToken ct)
    {
        if (!_cursors.TryGetValue(asset, out var since))
        {
            var floor = clock.GetUtcNow() - TimeSpan.FromMinutes(o.TradesLookbackMinutes);
            var from = earliestNeeded > floor ? earliestNeeded : floor;
            since = from.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var all = new List<PublicTrade>();
        for (var page = 0; page < o.TradesMaxPages; page++)
        {
            var result = await kraken.GetTradesAsync(asset, since, ct);
            all.AddRange(result.Trades);
            since = result.Last;
            if (result.Trades.Count < KrakenClient.TradesPageSize) break;
            if (page == o.TradesMaxPages - 1)
                log.LogWarning("{Asset}: trade tape still behind after {Pages} pages; continuing next cycle", asset, o.TradesMaxPages);
        }
        _cursors[asset] = since;
        return all;
    }

    /// <summary>Drop cursors for assets we no longer watch so a later order starts from its own placement time.</summary>
    public void Retain(IEnumerable<string> assets)
    {
        var keep = new HashSet<string>(assets, StringComparer.OrdinalIgnoreCase);
        foreach (var key in _cursors.Keys.Where(k => !keep.Contains(k)).ToList()) _cursors.Remove(key);
    }
}
