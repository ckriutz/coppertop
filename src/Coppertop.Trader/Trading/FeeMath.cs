namespace Coppertop.Trader.Trading;

public static class FeeMath
{
    public static decimal Fee(decimal notionalUsd, decimal feePct) => notionalUsd * feePct / 100m;

    // Smallest take-profit % that still nets MinNetProfitPct after paying maker fees on both legs.
    public static decimal RequiredTakeProfitPct(decimal makerFeePct, decimal minNetProfitPct) =>
        2m * makerFeePct + minNetProfitPct;

    public static decimal NetPnl(decimal entryPrice, decimal exitPrice, decimal volume, decimal entryFeePct, decimal exitFeePct)
    {
        var buy = entryPrice * volume;
        var sell = exitPrice * volume;
        return sell - buy - Fee(buy, entryFeePct) - Fee(sell, exitFeePct);
    }
}

public static class Indicators
{
    public static (decimal Sma, decimal StdDev) SmaAndStdDev(IReadOnlyList<decimal> closes, int period)
    {
        if (period <= 1 || closes.Count < period)
            throw new ArgumentException($"Need at least {period} closes (have {closes.Count}).", nameof(closes));

        var window = closes.Skip(closes.Count - period).ToArray();
        var mean = window.Average();
        var variance = window.Sum(c => (c - mean) * (c - mean)) / period;
        return (mean, (decimal)Math.Sqrt((double)variance));
    }
}
