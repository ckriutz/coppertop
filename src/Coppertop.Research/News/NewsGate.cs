using System.Collections.Concurrent;

namespace Coppertop.Research.News;

public enum NewsStatus { Disabled, Clear, Blocked, Unavailable }

public sealed record NewsOutcome(NewsStatus Status, string Reason, SonarResult? Fresh = null);

/// <summary>
/// Decides whether to spend a Sonar call on a coin: reuses a recent verdict, enforces the daily budget, and turns
/// failures into "unavailable" rather than throwing. Keeps its cache across cycles (register as a singleton).
/// </summary>
public sealed class NewsGate(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (NewsOutcome Outcome, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<NewsOutcome> CheckAsync(
        string asset,
        bool enabled,
        decimal spentTodayUsd,
        ResearchOptions o,
        decimal estimatedCostUsd,
        Func<string, CancellationToken, Task<SonarResult>> check,
        CancellationToken ct)
    {
        if (!enabled) return new NewsOutcome(NewsStatus.Disabled, "news check off (no OpenRouter key)");

        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(asset, out var hit) && now - hit.At < TimeSpan.FromHours(o.NewsCacheHours))
            return hit.Outcome with { Fresh = null };

        if (spentTodayUsd + estimatedCostUsd > o.NewsDailyBudgetUsd)
            return new NewsOutcome(NewsStatus.Unavailable, $"daily news budget ${o.NewsDailyBudgetUsd:0.00} used (${spentTodayUsd:0.000} spent)");

        SonarResult r;
        try
        {
            r = await check(asset, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new NewsOutcome(NewsStatus.Unavailable, $"news check failed: {ex.Message}");
        }

        var outcome = new NewsOutcome(r.Verdict.Block ? NewsStatus.Blocked : NewsStatus.Clear, r.Verdict.Reason, r);
        _cache[asset] = (outcome, now);
        return outcome;
    }
}
