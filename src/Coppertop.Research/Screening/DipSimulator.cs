using Coppertop.Research.Kraken;

namespace Coppertop.Research.Screening;

public sealed record DipSimSettings(
    int SmaPeriod,
    decimal BandStdDevs,
    decimal TakeProfitPct,
    decimal StopLossPct,
    decimal MakerFeePct,
    decimal TakerFeePct,
    decimal StopLossSlippagePct);

public sealed record DipSimResult(int Trades, int Wins, int Losses, int Open, decimal AvgNetPct, decimal AvgHoldMinutes)
{
    public int Resolved => Wins + Losses;
    public decimal? WinRatePct => Resolved == 0 ? null : 100m * Wins / Resolved;
}

/// <summary>
/// Replays the Trader's dip strategy over recent candles to see how it would have done on this asset.
/// Mirrors the Trader: buy when price trades below SMA − k·σ (maker), sell at +TP% (maker) or −SL% (taker, with
/// slippage). Deliberately pessimistic: exits aren't checked in the entry candle, and a candle that touches both TP
/// and SL counts as a loss. One position at a time; trades still open at the end are excluded from the averages.
/// Pure, no I/O.
/// </summary>
public static class DipSimulator
{
    public static DipSimResult Run(IReadOnlyList<Candle> candles, DipSimSettings s)
    {
        var closes = candles.Select(c => c.Close).ToArray();
        var interval = candles.Count > 1 ? candles[1].Time - candles[0].Time : TimeSpan.Zero;
        int wins = 0, losses = 0, open = 0;
        decimal netSum = 0, holdSum = 0;
        var buyCost = 1m + s.MakerFeePct / 100m;

        var i = s.SmaPeriod;
        while (i < candles.Count)
        {
            var (sma, sd) = Indicators.SmaAndStdDev(closes, i - s.SmaPeriod, s.SmaPeriod);
            var band = sma - s.BandStdDevs * sd;
            var c = candles[i];
            if (c.Low >= band) { i++; continue; }

            var entry = Math.Min(band, c.Open);
            var tp = entry * (1m + s.TakeProfitPct / 100m);
            var sl = entry * (1m - s.StopLossPct / 100m);
            var exitIndex = -1;
            for (var j = i + 1; j < candles.Count; j++)
            {
                var x = candles[j];
                if (x.Low <= sl)
                {
                    var exit = Math.Min(sl, x.Open) * (1m - s.StopLossSlippagePct / 100m);
                    netSum += exit * (1m - s.TakerFeePct / 100m) / (entry * buyCost) - 1m;
                    losses++;
                    exitIndex = j;
                    break;
                }
                if (x.High > tp)
                {
                    netSum += tp * (1m - s.MakerFeePct / 100m) / (entry * buyCost) - 1m;
                    wins++;
                    exitIndex = j;
                    break;
                }
            }

            if (exitIndex < 0)
            {
                open++;
                break;
            }
            holdSum += (decimal)((candles[exitIndex].Time - c.Time) + interval).TotalMinutes;
            i = exitIndex + 1;
        }

        var resolved = wins + losses;
        return new DipSimResult(
            resolved + open, wins, losses, open,
            resolved == 0 ? 0m : netSum / resolved * 100m,
            resolved == 0 ? 0m : holdSum / resolved);
    }

    /// <summary>Win rate needed to break even after fees, given this TP/SL.</summary>
    public static decimal BreakevenWinRatePct(DipSimSettings s)
    {
        var buyCost = 1m + s.MakerFeePct / 100m;
        var win = (1m + s.TakeProfitPct / 100m) * (1m - s.MakerFeePct / 100m) / buyCost - 1m;
        var loss = 1m - (1m - s.StopLossPct / 100m) * (1m - s.StopLossSlippagePct / 100m) * (1m - s.TakerFeePct / 100m) / buyCost;
        return 100m * loss / (win + loss);
    }
}
