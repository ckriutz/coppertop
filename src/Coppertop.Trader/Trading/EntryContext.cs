using Coppertop.Trader.Api;
using Coppertop.Trader.Kraken;

namespace Coppertop.Trader.Trading;

/// <summary>
/// What the Trader saw when it placed an entry, stored with the order so outcomes can be compared with conditions
/// (and with Research's prediction). Pure, no I/O. Field names are camelCase JSON on the wire.
/// </summary>
public static class EntryContext
{
    public static object Build(
        OpportunityDto opp,
        Ticker ticker,
        IReadOnlyList<decimal> closes,
        EntryPlan plan,
        PortfolioState portfolio,
        DateTimeOffset now,
        TraderOptions o)
    {
        var s = plan.Signal;
        var candlesPerHour = Math.Max(1, 60 / Math.Max(1, o.CandleIntervalMinutes));
        decimal? Change(int candlesBack) =>
            closes.Count > candlesBack && closes[^(candlesBack + 1)] != 0
                ? Math.Round((ticker.Last / closes[^(candlesBack + 1)] - 1m) * 100m, 4)
                : null;

        return new
        {
            version = 1,
            placedAt = now,
            hourUtc = now.UtcDateTime.Hour,
            dayOfWeek = now.UtcDateTime.DayOfWeek.ToString(),
            bid = ticker.Bid,
            ask = ticker.Ask,
            last = ticker.Last,
            spreadPct = Math.Round(ticker.SpreadPct, 4),
            sma = s?.Sma,
            stdDev = s?.StdDev,
            band = s?.Band,
            askBelowBandPct = s is { Band: > 0 } ? Math.Round((s.Band - ticker.Ask) / s.Band * 100m, 4) : (decimal?)null,
            zScore = s is { StdDev: > 0 } ? Math.Round((ticker.Ask - s.Sma) / s.StdDev, 3) : (decimal?)null,
            rsi14 = Indicators.Rsi(closes) is { } rsi ? Math.Round(rsi, 2) : (decimal?)null,
            change1hPct = Change(candlesPerHour),
            change4hPct = Change(candlesPerHour * 4),
            entryPrice = plan.EntryPrice,
            volume = plan.Volume,
            spendUsd = plan.SpendUsd,
            takeProfitPrice = plan.TakeProfitPrice,
            stopLossPrice = plan.StopLossPrice,
            takeProfitPct = opp.TakeProfitPct,
            stopLossPct = opp.StopLossPct,
            opportunityConfidence = opp.Confidence,
            opportunityAgeMinutes = opp.CreatedAt is { } created ? Math.Round((decimal)(now - created).TotalMinutes, 1) : (decimal?)null,
            openPositions = portfolio.OpenPositions,
            availableCashUsd = Math.Round(portfolio.AvailableCashUsd, 2),
            candleIntervalMinutes = o.CandleIntervalMinutes,
            smaPeriod = o.SmaPeriod,
            bandStdDevs = o.BandStdDevs,
        };
    }
}
