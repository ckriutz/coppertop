using Coppertop.Trader.Api;
using Coppertop.Trader.Kraken;

namespace Coppertop.Trader.Trading;

public sealed record EntryPlan(
    decimal EntryPrice,
    decimal Volume,
    decimal SpendUsd,
    decimal TakeProfitPrice,
    decimal StopLossPrice,
    string Rationale,
    DipSignal? Signal = null);

/// <summary>The band the entry was measured against, kept for the entry's recorded context.</summary>
public sealed record DipSignal(decimal Sma, decimal StdDev, decimal Band);

public sealed record EntryDecision(EntryPlan? Plan, string Reason)
{
    public static EntryDecision Skip(string reason) => new(null, reason);
}

public sealed record PortfolioState(decimal AvailableCashUsd, int OpenPositions, IReadOnlyDictionary<string, decimal> ExposureByAsset);

/// <summary>
/// Mean-reversion "buy the dip": enter with a post-only limit at the bid when the ask
/// is below SMA - k·σ, and exit at +TP% (maker) or -SL% (taker). Pure function, no I/O.
/// </summary>
public static class DipStrategy
{
    public static EntryDecision Evaluate(
        OpportunityDto opportunity,
        Ticker ticker,
        IReadOnlyList<decimal> closes,
        PairInfo pair,
        PortfolioState portfolio,
        TraderOptions o)
    {
        if (opportunity.Strategy != "dip")
            return EntryDecision.Skip($"strategy '{opportunity.Strategy}' not supported");

        var requiredTp = FeeMath.RequiredTakeProfitPct(o.MakerFeePct, o.MinNetProfitPct);
        if (opportunity.TakeProfitPct < requiredTp)
            return EntryDecision.Skip($"take-profit {opportunity.TakeProfitPct}% < fee-adjusted minimum {requiredTp}%");

        if (ticker.SpreadPct > o.MaxSpreadPct)
            return EntryDecision.Skip($"spread {ticker.SpreadPct:F3}% > max {o.MaxSpreadPct}%");

        if (portfolio.OpenPositions >= o.MaxOpenPositions)
            return EntryDecision.Skip($"max open positions ({o.MaxOpenPositions}) reached");

        if (closes.Count < o.SmaPeriod)
            return EntryDecision.Skip($"not enough candles ({closes.Count}/{o.SmaPeriod})");

        var (sma, sd) = Indicators.SmaAndStdDev(closes, o.SmaPeriod);
        var band = sma - o.BandStdDevs * sd;
        if (ticker.Ask > band)
            return EntryDecision.Skip($"ask {ticker.Ask} above dip band {band:F8} (sma {sma:F8})");

        if (ticker.Ask > opportunity.MaxEntryPrice)
            return EntryDecision.Skip($"ask {ticker.Ask} above research max entry {opportunity.MaxEntryPrice}");

        var existing = portfolio.ExposureByAsset.GetValueOrDefault(opportunity.Asset);
        var spend = Math.Min(Math.Min(opportunity.MaxSpendUsd, o.MaxTradeUsd), portfolio.AvailableCashUsd);
        spend = Math.Min(spend, o.MaxPerAssetExposureUsd - existing);
        if (spend < o.MinTradeUsd)
            return EntryDecision.Skip($"size ${spend:F2} below minimum ${o.MinTradeUsd}");

        var entry = RoundDown(ticker.Bid, pair.PriceDecimals);
        // Leave room for the entry fee so total cost stays within the spend cap.
        var volume = RoundDown(spend / (entry * (1m + o.MakerFeePct / 100m)), pair.LotDecimals);
        if (volume <= 0 || volume < pair.OrderMin)
            return EntryDecision.Skip($"volume {volume} below Kraken minimum {pair.OrderMin}");
        if (pair.CostMin > 0 && entry * volume < pair.CostMin)
            return EntryDecision.Skip($"cost {entry * volume:F2} below Kraken minimum {pair.CostMin}");

        var (tp, sl) = ExitPrices(entry, opportunity.TakeProfitPct, opportunity.StopLossPct, pair.PriceDecimals);

        return new EntryDecision(
            new EntryPlan(entry, volume, entry * volume, tp, sl,
                $"ask {ticker.Ask} <= band {band:F8}; tp {tp} sl {sl}", new DipSignal(sma, sd, band)),
            "enter");
    }

    // TP rounds up and SL rounds down so rounding never makes the trade look better than planned.
    public static (decimal TakeProfit, decimal StopLoss) ExitPrices(decimal entry, decimal takeProfitPct, decimal stopLossPct, int priceDecimals) =>
        (RoundUp(entry * (1m + takeProfitPct / 100m), priceDecimals),
         RoundDown(entry * (1m - stopLossPct / 100m), priceDecimals));

    private static decimal RoundDown(decimal v, int decimals) => Math.Round(v, decimals, MidpointRounding.ToZero);

    private static decimal RoundUp(decimal v, int decimals)
    {
        var factor = (decimal)Math.Pow(10, decimals);
        return Math.Ceiling(v * factor) / factor;
    }
}
