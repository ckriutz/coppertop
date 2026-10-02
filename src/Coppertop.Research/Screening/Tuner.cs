using Coppertop.Research.Kraken;

namespace Coppertop.Research.Screening;

public sealed record TuneResult(DipSimSettings Settings, int? MaxHoldHours, DipSimResult Train, DipSimResult Test, int SplitIndex, int CombosTested);

/// <summary>
/// Per-coin tuning with a walk-forward check. Tries every TP / SL / band / time-stop combination on the older part of
/// the candles (train), keeps the one that made the most in total, then replays only that one on the newer part
/// (test) it never saw. Picking the best of many on the same data always looks good, so only the test result says
/// whether the setting is real. Returns null when nothing made money in training. Pure, no I/O.
/// </summary>
public static class Tuner
{
    public static readonly decimal[] DefaultTakeProfitPcts = [1.0m, 1.5m, 2.0m, 2.5m, 3.0m];
    public static readonly decimal[] DefaultStopLossPcts = [1.0m, 1.5m, 2.0m, 3.0m];
    public static readonly decimal[] DefaultBandStdDevs = [1.5m, 2.0m, 2.5m];
    public static readonly int[] DefaultMaxHoldHours = [4, 12, 24, 0];

    public static TuneResult? Tune(IReadOnlyList<Candle> candles, ResearchOptions o)
    {
        var split = SplitIndex(candles.Count, o);
        if (split <= o.SmaPeriod || split >= candles.Count) return null;
        var intervalMinutes = candles.Count > 1 ? (candles[1].Time - candles[0].Time).TotalMinutes : o.CandleIntervalMinutes;

        (DipSimSettings Settings, int? HoldHours, DipSimResult Train)? best = null;
        var combos = 0;
        foreach (var (settings, holdHours) in Grid(o, intervalMinutes))
        {
            combos++;
            var train = DipSimulator.Run(candles, settings, 0, split);
            if (train.Resolved < o.MinSimTrades || train.TotalNetPct <= 0) continue;
            if (best is { } b && !Better(train, b.Train)) continue;
            best = (settings, holdHours, train);
        }
        if (best is not { } chosen) return null;

        var test = DipSimulator.Run(candles, chosen.Settings, split);
        return new TuneResult(chosen.Settings, chosen.HoldHours, chosen.Train, test, split, combos);
    }

    public static int SplitIndex(int count, ResearchOptions o) =>
        (int)Math.Round(count * Math.Clamp(o.TuneTrainFraction, 0.1m, 0.9m));

    /// <summary>Every combination worth trying; TPs too small to clear fees by MinNetProfitPct are skipped.</summary>
    public static IEnumerable<(DipSimSettings Settings, int? HoldHours)> Grid(ResearchOptions o, double intervalMinutes)
    {
        var tps = Or(o.TuneTakeProfitPcts, DefaultTakeProfitPcts);
        var sls = Or(o.TuneStopLossPcts, DefaultStopLossPcts);
        var bands = Or(o.TuneBandStdDevs, DefaultBandStdDevs);
        var holds = Or(o.TuneMaxHoldHours, DefaultMaxHoldHours);
        var buyCost = 1m + o.MakerFeePct / 100m;

        foreach (var tp in tps)
        {
            var winNetPct = ((1m + tp / 100m) * (1m - o.MakerFeePct / 100m) / buyCost - 1m) * 100m;
            if (winNetPct < o.MinNetProfitPct) continue;
            foreach (var sl in sls)
            foreach (var k in bands)
            foreach (var h in holds)
            {
                var holdCandles = h > 0 ? (int)Math.Ceiling(h * 60 / intervalMinutes) : 0;
                yield return (new DipSimSettings(o.SmaPeriod, k, tp, sl, o.MakerFeePct, o.TakerFeePct, o.StopLossSlippagePct, holdCandles),
                    h > 0 ? h : null);
            }
        }
    }

    private static bool Better(DipSimResult a, DipSimResult b) =>
        a.TotalNetPct != b.TotalNetPct ? a.TotalNetPct > b.TotalNetPct
        : a.AvgNetPct != b.AvgNetPct ? a.AvgNetPct > b.AvgNetPct
        : a.AvgHoldMinutes < b.AvgHoldMinutes;

    private static IReadOnlyList<T> Or<T>(List<T> configured, T[] defaults) => configured.Count > 0 ? configured : defaults;
}
