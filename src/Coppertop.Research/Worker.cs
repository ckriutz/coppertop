using Coppertop.Research.Api;
using Coppertop.Research.Kraken;
using Coppertop.Research.News;
using Coppertop.Research.Screening;
using Microsoft.Extensions.Options;

namespace Coppertop.Research;

/// <summary>
/// Research cycle: screen the watchlist with plain code (free), ask Sonar about recent bad news for the few coins
/// that pass (paid, budgeted), publish opportunities for the survivors, veto coins with bad news, cancel
/// opportunities for assets that no longer pass, and post every verdict to the Api for the dashboard.
/// </summary>
public sealed class Worker(
    IServiceScopeFactory scopes,
    IOptions<ResearchOptions> options,
    NewsGate news,
    TimeProvider clock,
    ILogger<Worker> log) : BackgroundService
{
    private const string Strategy = "dip";
    private const string Service = "research";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        log.LogInformation("Research starting: screening {Count} assets every {Minutes} min", o.Watchlist.Count, o.CycleMinutes);
        using (var scope = scopes.CreateScope())
        {
            if (!scope.ServiceProvider.GetRequiredService<SonarClient>().Enabled)
                log.LogWarning("OpenRouter:ApiKey is not set; news vetoes are off and screened coins are published unchecked.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(o.CycleMinutes);
            try
            {
                using var scope = scopes.CreateScope();
                await ResearchCycleAsync(
                    scope.ServiceProvider.GetRequiredService<KrakenPublicClient>(),
                    scope.ServiceProvider.GetRequiredService<CoppertopApiClient>(),
                    scope.ServiceProvider.GetRequiredService<SonarClient>(),
                    o, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Don't wait a full cycle after a failure (e.g. Api still starting); retry soon.
                delay = TimeSpan.FromMinutes(Math.Min(o.RetryMinutes, o.CycleMinutes));
                log.LogError(ex, "Research cycle failed, retrying in {Minutes} min", delay.TotalMinutes);
            }

            try { await Task.Delay(delay, clock, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ResearchCycleAsync(KrakenPublicClient kraken, CoppertopApiClient api, SonarClient sonar, ResearchOptions o, CancellationToken ct)
    {
        if (o.Watchlist.Count == 0)
        {
            log.LogWarning("Watchlist is empty; nothing to research.");
            return;
        }

        var tickers = await kraken.GetTickersAsync(o.Watchlist, ct);
        var results = new List<ScreenResult>();
        foreach (var asset in o.Watchlist)
        {
            if (!tickers.TryGetValue(asset, out var ticker))
            {
                results.Add(ScreenResult.Failed(asset, "no ticker from Kraken"));
                continue;
            }
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(o.KrakenRequestDelayMs), clock, ct);
                var fast = await kraken.GetCandlesAsync(asset, o.CandleIntervalMinutes, ct);
                await Task.Delay(TimeSpan.FromMilliseconds(o.KrakenRequestDelayMs), clock, ct);
                var hourly = await kraken.GetCandlesAsync(asset, 60, ct);
                results.Add(Screener.Screen(asset, ticker, fast, hourly, o));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning("{Asset}: screening failed: {Message}", asset, ex.Message);
                results.Add(ScreenResult.Failed(asset, $"data error: {ex.Message}"));
            }
        }

        var ranked = Screener.Rank(results, o.MaxOpportunities).ToList();
        var news = await ApplyNewsAsync(ranked, api, sonar, o, ct);
        var screenedAt = clock.GetUtcNow();
        var expires = screenedAt.AddHours(o.OpportunityLifetimeHours);
        var published = 0;

        foreach (var r in ranked.Where(r => r.Approved))
        {
            var opp = await api.PublishOpportunityAsync(new CreateOpportunityDto(
                r.Asset, Strategy, r.Metrics!.LastPrice, o.TakeProfitPct, o.StopLossPct, o.MaxSpendUsd,
                r.Confidence, r.Reason, expires, OpportunityContext(r, news.GetValueOrDefault(r.Asset), screenedAt, o)), ct);
            if (opp is null)
            {
                log.LogInformation("{Asset}: approved but vetoed, not published", r.Asset);
                continue;
            }
            published++;
            log.LogInformation("{Asset}: APPROVED (confidence {Confidence:P0}) {Reason}", r.Asset, r.Confidence, r.Reason);
        }

        // An asset that no longer passes shouldn't keep a live opportunity from an earlier cycle.
        var approved = ranked.Where(r => r.Approved).Select(r => r.Asset).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in (await api.GetActiveOpportunitiesAsync(ct)).Where(x => x.Strategy == Strategy && !approved.Contains(x.Asset)))
        {
            await api.CancelOpportunityAsync(stale.Id, ct);
            log.LogInformation("{Asset}: cancelled opportunity {Id}, no longer passes screening", stale.Asset, stale.Id);
        }

        foreach (var r in ranked.Where(r => !r.Approved))
            log.LogInformation("{Asset}: rejected: {Reason}", r.Asset, r.Reason);

        await api.PublishScreenAsync(new PublishScreenDto(
            ranked.Select(r => new ScreenResultDto(r.Asset, r.Approved, r.Confidence, r.Reason, r.Metrics)).ToList()), ct);
        log.LogInformation("Research cycle done: {Published} published, {Rejected} rejected", published, ranked.Count(r => !r.Approved));
    }

    /// <summary>
    /// Checks each approved coin for recent bad news. Bad news vetoes the coin (which also cancels its opportunities)
    /// and rejects it; a coin that couldn't be checked is held back unless PublishWhenNewsUnavailable is set.
    /// </summary>
    private async Task<Dictionary<string, NewsOutcome>> ApplyNewsAsync(List<ScreenResult> ranked, CoppertopApiClient api, SonarClient sonar, ResearchOptions o, CancellationToken ct)
    {
        var outcomes = new Dictionary<string, NewsOutcome>(StringComparer.OrdinalIgnoreCase);
        if (!ranked.Any(r => r.Approved)) return outcomes;

        var vetoes = (await api.GetActiveVetoesAsync(ct)).GroupBy(v => v.Asset, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var dayStart = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        var spent = sonar.Enabled
            ? (await api.GetTokenUsageAsync(ct)).Where(t => t.Service == Service && t.CreatedAt >= dayStart).Sum(t => t.CostUsd)
            : 0m;
        var estimate = sonar.EstimatedCallCostUsd;

        for (var i = 0; i < ranked.Count; i++)
        {
            var r = ranked[i];
            if (!r.Approved) continue;

            if (vetoes.TryGetValue(r.Asset, out var veto))
            {
                ranked[i] = r with { Approved = false, Reason = $"vetoed until {veto.ExpiresAt:MM-dd HH:mm}Z: {veto.Reason} | {r.Reason}" };
                continue;
            }

            var outcome = await news.CheckAsync(r.Asset, sonar.Enabled, spent, o, estimate,
                (asset, token) => sonar.CheckNewsAsync(asset, o.NewsLookbackHours, token), ct);
            outcomes[r.Asset] = outcome;

            if (outcome.Fresh is { } fresh)
            {
                spent += fresh.CostUsd;
                await api.RecordTokenUsageAsync(new CreateTokenUsageDto(Service, fresh.Model, $"news-check {r.Asset}",
                    fresh.InputTokens, fresh.OutputTokens, fresh.CostUsd), ct);
                log.LogInformation("{Asset}: Sonar says {Verdict} (${Cost:0.0000}): {Reason} {Sources}", r.Asset,
                    fresh.Verdict.Block ? "BLOCK" : "allow", fresh.CostUsd, fresh.Verdict.Reason, string.Join(" ", fresh.Sources));
            }

            switch (outcome.Status)
            {
                case NewsStatus.Blocked:
                    await api.PublishVetoAsync(new CreateVetoDto(r.Asset, $"news: {outcome.Reason}",
                        clock.GetUtcNow().AddHours(o.NewsVetoHours)), ct);
                    ranked[i] = r with { Approved = false, Reason = $"news veto: {outcome.Reason} | {r.Reason}" };
                    log.LogWarning("{Asset}: vetoed for {Hours}h on news: {Reason}", r.Asset, o.NewsVetoHours, outcome.Reason);
                    break;
                case NewsStatus.Unavailable when !o.PublishWhenNewsUnavailable:
                    ranked[i] = r with { Approved = false, Reason = $"{outcome.Reason}; held back | {r.Reason}" };
                    log.LogWarning("{Asset}: {Reason}; not publishing", r.Asset, outcome.Reason);
                    break;
                case NewsStatus.Unavailable:
                case NewsStatus.Clear:
                    ranked[i] = r with { Reason = $"{r.Reason} | news: {outcome.Reason}" };
                    break;
            }
        }
        return outcomes;
    }

    /// <summary>
    /// What Research knew when it published: the screen metrics (incl. the replay's prediction), the news verdict
    /// and the settings the replay used. Stored with the opportunity so trades can be compared with predictions.
    /// </summary>
    public static object OpportunityContext(ScreenResult r, NewsOutcome? news, DateTimeOffset screenedAt, ResearchOptions o) => new
    {
        version = 1,
        screenedAt,
        confidence = r.Confidence,
        screen = r.Metrics,
        news = news is null ? null : new { status = news.Status.ToString().ToLowerInvariant(), reason = news.Reason },
        replay = new
        {
            o.CandleIntervalMinutes, o.SmaPeriod, o.BandStdDevs, o.TakeProfitPct, o.StopLossPct,
            o.MakerFeePct, o.TakerFeePct, o.StopLossSlippagePct,
        },
    };
}
