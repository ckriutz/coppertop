using Coppertop.Research.Kraken;

namespace Coppertop.Research.Screening;

// Research's own copy of the indicator maths. Keep SmaAndStdDev identical to the Trader's (population σ).
public static class Indicators
{
    public static (decimal Sma, decimal StdDev) SmaAndStdDev(IReadOnlyList<decimal> values, int start, int period)
    {
        decimal sum = 0;
        for (var i = start; i < start + period; i++) sum += values[i];
        var mean = sum / period;
        decimal sq = 0;
        for (var i = start; i < start + period; i++) sq += (values[i] - mean) * (values[i] - mean);
        return (mean, (decimal)Math.Sqrt((double)(sq / period)));
    }

    public static decimal Sma(IReadOnlyList<decimal> values, int period) =>
        values.Skip(values.Count - period).Average();

    /// <summary>Wilder's RSI over the whole series (seeded with the first <paramref name="period"/> changes).</summary>
    public static decimal Rsi(IReadOnlyList<decimal> closes, int period = 14)
    {
        if (closes.Count <= period) throw new ArgumentException($"Need more than {period} closes.", nameof(closes));
        decimal gain = 0, loss = 0;
        for (var i = 1; i <= period; i++)
        {
            var d = closes[i] - closes[i - 1];
            if (d > 0) gain += d; else loss -= d;
        }
        gain /= period;
        loss /= period;
        for (var i = period + 1; i < closes.Count; i++)
        {
            var d = closes[i] - closes[i - 1];
            gain = (gain * (period - 1) + Math.Max(d, 0)) / period;
            loss = (loss * (period - 1) + Math.Max(-d, 0)) / period;
        }
        if (loss == 0) return gain == 0 ? 50m : 100m;
        return 100m - 100m / (1m + gain / loss);
    }

    /// <summary>Average true range of the last <paramref name="period"/> candles, as a % of the last close.</summary>
    public static decimal AtrPct(IReadOnlyList<Candle> candles, int period = 14)
    {
        if (candles.Count <= period) throw new ArgumentException($"Need more than {period} candles.", nameof(candles));
        decimal sum = 0;
        for (var i = candles.Count - period; i < candles.Count; i++)
        {
            var c = candles[i];
            var prev = candles[i - 1].Close;
            sum += Math.Max(c.High - c.Low, Math.Max(Math.Abs(c.High - prev), Math.Abs(c.Low - prev)));
        }
        return sum / period / candles[^1].Close * 100m;
    }

    /// <summary>Lower bound of the 95% Wilson interval: the win rate we can be fairly sure of, given few samples.</summary>
    public static decimal WilsonLower(int wins, int total, double z = 1.96)
    {
        if (total == 0) return 0m;
        var p = (double)wins / total;
        var z2 = z * z;
        var centre = p + z2 / (2 * total);
        var margin = z * Math.Sqrt(p * (1 - p) / total + z2 / (4.0 * total * total));
        return (decimal)Math.Max(0, (centre - margin) / (1 + z2 / total));
    }
}
