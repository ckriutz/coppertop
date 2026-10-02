using Coppertop.Research.Kraken;

namespace Coppertop.Research.Screening;

public sealed record SimMetrics(
    int Trades, int Wins, int Losses, int TimeStops, int Open,
    decimal? WinRatePct, decimal BreakevenWinRatePct, decimal AvgNetPct, decimal AvgHoldMinutes, decimal Hours)
{
    public int Resolved => Wins + Losses + TimeStops;

    public static SimMetrics From(DipSimResult r, DipSimSettings s, decimal hours) =>
        new(r.Trades, r.Wins, r.Losses, r.TimeStops, r.Open, r.WinRatePct,
            DipSimulator.BreakevenWinRatePct(s), r.AvgNetPct, r.AvgHoldMinutes, hours);
}

/// <summary>The setting the tuner picked for this coin, with how it did in training and on the unseen test part.</summary>
public sealed record TunedMetrics(
    int CandleIntervalMinutes, int SmaPeriod, decimal BandStdDevs, decimal TakeProfitPct, decimal StopLossPct,
    int? MaxHoldHours, int CombosTested, SimMetrics Train, SimMetrics Test);

public sealed record ScreenMetrics(
    decimal LastPrice,
    decimal SpreadPct,
    decimal Volume24hUsd,
    decimal? Change24hPct,
    decimal? Change7dPct,
    decimal? Rsi14,
    decimal? AtrPct,
    string Trend,
    SimMetrics? Sim,
    TunedMetrics? Tuned = null);

public sealed record ScreenResult(string Asset, bool Approved, decimal Confidence, string Reason, ScreenMetrics? Metrics)
{
    public static ScreenResult Failed(string asset, string reason) => new(asset, false, 0m, reason, null);
}

/// <summary>
/// Step 1 of Research: plain-code screening, no LLM. Measures each asset, tunes the dip strategy for it on the older
/// ~2/3 of the last ~7.5 days of 15m candles and checks the chosen setting on the newer ~1/3 it never saw. An asset is
/// approved only if it passes every hard rule and the tuned setting also made money on the unseen part. Sim is the
/// chosen setting replayed over the whole period (or the default setting when nothing passed training). Pure, no I/O.
/// </summary>
public static class Screener
{
    public static ScreenResult Screen(
        string asset,
        Ticker ticker,
        IReadOnlyList<Candle> fast,
        IReadOnlyList<Candle> hourly,
        ResearchOptions o)
    {
        // Kraken's last candle is still forming; only replay closed ones.
        var closedFast = fast.Take(Math.Max(0, fast.Count - 1)).ToList();
        SimMetrics? sim = null;
        TunedMetrics? tuned = null;
        if (closedFast.Count > o.SmaPeriod + 1)
        {
            decimal Hours(int from, int to) => to - 1 <= from ? 0m : (decimal)(closedFast[to - 1].Time - closedFast[from].Time).TotalHours;
            var interval = (int)(closedFast[1].Time - closedFast[0].Time).TotalMinutes;
            var tune = Tuner.Tune(closedFast, o);
            var settings = tune?.Settings ?? DefaultSettings(o);
            sim = SimMetrics.From(DipSimulator.Run(closedFast, settings), settings, Hours(0, closedFast.Count));
            if (tune is not null)
                tuned = new TunedMetrics(interval, settings.SmaPeriod, settings.BandStdDevs, settings.TakeProfitPct,
                    settings.StopLossPct, tune.MaxHoldHours, tune.CombosTested,
                    SimMetrics.From(tune.Train, settings, Hours(0, tune.SplitIndex)),
                    SimMetrics.From(tune.Test, settings, Hours(tune.SplitIndex, closedFast.Count)));
        }

        var closes = hourly.Select(c => c.Close).ToList();
        var last = ticker.Last;
        decimal? Change(int hoursBack) => closes.Count > hoursBack ? (last / closes[^(hoursBack + 1)] - 1m) * 100m : null;
        var trend = Trend(closes, last);
        var metrics = new ScreenMetrics(
            last,
            ticker.SpreadPct,
            ticker.Volume24hUsd,
            Change(24),
            Change(168),
            closes.Count > 15 ? Indicators.Rsi(closes) : null,
            hourly.Count > 15 ? Indicators.AtrPct(hourly) : null,
            trend,
            sim,
            tuned);

        var problems = new List<string>();
        if (ticker.SpreadPct > o.MaxSpreadPct)
            problems.Add($"spread {ticker.SpreadPct:F2}% > {o.MaxSpreadPct}%");
        if (ticker.Volume24hUsd < o.MinVolume24hUsd)
            problems.Add($"24h volume ${ticker.Volume24hUsd:N0} < ${o.MinVolume24hUsd:N0}");
        if (metrics.Change24hPct < -o.MaxDrop24hPct)
            problems.Add($"falling knife: {metrics.Change24hPct:F1}% in 24h");
        if (o.RejectDowntrend && trend == "down")
            problems.Add("hourly downtrend (price < SMA50 < SMA200)");
        if (sim is null)
            problems.Add("not enough candles to replay");
        else if (tuned is null)
            problems.Add($"no dip setting made money in training (needs {o.MinSimTrades}+ closed trades)");
        else if (tuned.Test.Resolved < o.MinTestTrades)
            problems.Add($"too few dips to judge on unseen data ({tuned.Test.Resolved} closed in test, need {o.MinTestTrades})");
        else if (tuned.Test.AvgNetPct <= o.MinSimAvgNetPct)
            problems.Add($"tuned setting lost on unseen data: {Record(tuned.Test)}, avg {tuned.Test.AvgNetPct:+0.00;-0.00}%/trade after fees");

        var confidence = sim is null || sim.Resolved == 0 ? 0m : Math.Round(Indicators.WilsonLower(sim.Wins, sim.Resolved), 3);
        var summary = Summary(sim, tuned, trend);
        if (metrics.Rsi14 is { } rsi) summary += $", RSI {rsi:F0}";

        return problems.Count == 0
            ? new ScreenResult(asset, true, confidence, summary, metrics)
            : new ScreenResult(asset, false, confidence, string.Join("; ", problems) + " | " + summary, metrics);
    }

    public static DipSimSettings DefaultSettings(ResearchOptions o) =>
        new(o.SmaPeriod, o.BandStdDevs, o.TakeProfitPct, o.StopLossPct, o.MakerFeePct, o.TakerFeePct, o.StopLossSlippagePct);

    private static string Record(SimMetrics m) => m.TimeStops > 0 ? $"{m.Wins}W/{m.Losses}L/{m.TimeStops}T" : $"{m.Wins}W/{m.Losses}L";

    private static string Summary(SimMetrics? sim, TunedMetrics? t, string trend)
    {
        if (sim is null) return $"trend {trend}";
        if (t is null)
            return $"default dip {sim.Hours:F0}h: {Record(sim)}, avg {sim.AvgNetPct:+0.00;-0.00}%/trade; trend {trend}";
        var hold = t.MaxHoldHours is { } h ? $"{h}h" : "none";
        return $"tuned TP {t.TakeProfitPct}%/SL {t.StopLossPct}%/k {t.BandStdDevs}/hold {hold} (best of {t.CombosTested}): " +
               $"train {t.Train.Hours:F0}h {Record(t.Train)} avg {t.Train.AvgNetPct:+0.00;-0.00}%, " +
               $"test {t.Test.Hours:F0}h {Record(t.Test)} avg {t.Test.AvgNetPct:+0.00;-0.00}%, " +
               $"~{sim.AvgHoldMinutes:F0} min hold; trend {trend}";
    }

    /// <summary>Keep the best <paramref name="max"/> approvals by average net return on unseen data; demote the rest.</summary>
    public static IReadOnlyList<ScreenResult> Rank(IEnumerable<ScreenResult> results, int max)
    {
        var list = results.ToList();
        var keep = list.Where(r => r.Approved)
            .OrderByDescending(r => RankScore(r) ?? 0m).ThenByDescending(r => r.Confidence)
            .Take(max).Select(r => r.Asset).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list
            .Select(r => r.Approved && !keep.Contains(r.Asset)
                ? r with { Approved = false, Reason = $"ranked below the top {max} | {r.Reason}" }
                : r)
            .OrderByDescending(r => r.Approved).ThenByDescending(r => RankScore(r) ?? decimal.MinValue)
            .ToList();
    }

    /// <summary>Unseen-data result when tuned (the honest number), else the full replay.</summary>
    private static decimal? RankScore(ScreenResult r) => r.Metrics?.Tuned?.Test.AvgNetPct ?? r.Metrics?.Sim?.AvgNetPct;

    public static string Trend(IReadOnlyList<decimal> hourlyCloses, decimal last)
    {
        if (hourlyCloses.Count < 200) return "unknown";
        var sma50 = Indicators.Sma(hourlyCloses, 50);
        var sma200 = Indicators.Sma(hourlyCloses, 200);
        if (last > sma50 && sma50 > sma200) return "up";
        if (last < sma50 && sma50 < sma200) return "down";
        return "flat";
    }
}
