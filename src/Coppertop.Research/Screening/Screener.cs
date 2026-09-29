using Coppertop.Research.Kraken;

namespace Coppertop.Research.Screening;

public sealed record SimMetrics(
    int Trades, int Wins, int Losses, int Open,
    decimal? WinRatePct, decimal BreakevenWinRatePct, decimal AvgNetPct, decimal AvgHoldMinutes, decimal Hours);

public sealed record ScreenMetrics(
    decimal LastPrice,
    decimal SpreadPct,
    decimal Volume24hUsd,
    decimal? Change24hPct,
    decimal? Change7dPct,
    decimal? Rsi14,
    decimal? AtrPct,
    string Trend,
    SimMetrics? Sim);

public sealed record ScreenResult(string Asset, bool Approved, decimal Confidence, string Reason, ScreenMetrics? Metrics)
{
    public static ScreenResult Failed(string asset, string reason) => new(asset, false, 0m, reason, null);
}

/// <summary>
/// Step 1 of Research: plain-code screening, no LLM. Measures each asset and replays the Trader's dip strategy on the
/// last ~60h of 5m candles. An asset is approved only if it passes every hard rule and the replay made money after
/// fees. Pure, no I/O.
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
        var settings = new DipSimSettings(o.SmaPeriod, o.BandStdDevs, o.TakeProfitPct, o.StopLossPct,
            o.MakerFeePct, o.TakerFeePct, o.StopLossSlippagePct);

        // Kraken's last candle is still forming; only replay closed ones.
        var closedFast = fast.Take(Math.Max(0, fast.Count - 1)).ToList();
        SimMetrics? sim = null;
        if (closedFast.Count > o.SmaPeriod + 1)
        {
            var r = DipSimulator.Run(closedFast, settings);
            var hours = (decimal)(closedFast[^1].Time - closedFast[0].Time).TotalHours;
            sim = new SimMetrics(r.Trades, r.Wins, r.Losses, r.Open, r.WinRatePct,
                DipSimulator.BreakevenWinRatePct(settings), r.AvgNetPct, r.AvgHoldMinutes, hours);
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
            sim);

        var problems = new List<string>();
        if (ticker.SpreadPct > o.MaxSpreadPct)
            problems.Add($"spread {ticker.SpreadPct:F2}% > {o.MaxSpreadPct}%");
        if (ticker.Volume24hUsd < o.MinVolume24hUsd)
            problems.Add($"24h volume ${ticker.Volume24hUsd:N0} < ${o.MinVolume24hUsd:N0}");
        if (metrics.Change24hPct < -o.MaxDrop24hPct)
            problems.Add($"falling knife: {metrics.Change24hPct:F1}% in 24h");
        if (o.RejectDowntrend && trend == "down")
            problems.Add("hourly downtrend (price < SMA50 < SMA200)");
        if (sim is null || sim.Wins + sim.Losses < o.MinSimTrades)
            problems.Add($"too few dips to judge ({sim?.Wins + sim?.Losses ?? 0} closed in replay, need {o.MinSimTrades})");
        else if (sim.AvgNetPct <= o.MinSimAvgNetPct)
            problems.Add($"dip replay lost money: {sim.Wins}W/{sim.Losses}L, avg {sim.AvgNetPct:+0.00;-0.00}%/trade after fees");

        var confidence = sim is null ? 0m : Math.Round(Indicators.WilsonLower(sim.Wins, sim.Wins + sim.Losses), 3);
        var summary = sim is null
            ? $"trend {trend}"
            : $"dip replay {sim.Hours:F0}h: {sim.Wins}W/{sim.Losses}L (need {sim.BreakevenWinRatePct:F0}% to break even), " +
              $"avg {sim.AvgNetPct:+0.00;-0.00}%/trade, ~{sim.AvgHoldMinutes:F0} min hold; trend {trend}";
        if (metrics.Rsi14 is { } rsi) summary += $", RSI {rsi:F0}";

        return problems.Count == 0
            ? new ScreenResult(asset, true, confidence, summary, metrics)
            : new ScreenResult(asset, false, confidence, string.Join("; ", problems) + " | " + summary, metrics);
    }

    /// <summary>Keep the best <paramref name="max"/> approvals by average net return; demote the rest.</summary>
    public static IReadOnlyList<ScreenResult> Rank(IEnumerable<ScreenResult> results, int max)
    {
        var list = results.ToList();
        var keep = list.Where(r => r.Approved)
            .OrderByDescending(r => r.Metrics?.Sim?.AvgNetPct ?? 0m).ThenByDescending(r => r.Confidence)
            .Take(max).Select(r => r.Asset).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list
            .Select(r => r.Approved && !keep.Contains(r.Asset)
                ? r with { Approved = false, Reason = $"ranked below the top {max} | {r.Reason}" }
                : r)
            .OrderByDescending(r => r.Approved).ThenByDescending(r => r.Metrics?.Sim?.AvgNetPct ?? decimal.MinValue)
            .ToList();
    }

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
