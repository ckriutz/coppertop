using Coppertop.Research.Api;
using Coppertop.Research.Kraken;
using Coppertop.Research.Screening;
using Microsoft.Extensions.Options;

namespace Coppertop.Research;

/// <summary>
/// Research cycle, step 1 (no LLM): screen the watchlist with plain code, publish opportunities for the best few,
/// cancel opportunities for assets that no longer pass, and post every verdict to the Api for the dashboard.
/// </summary>
public sealed class Worker(
    IServiceScopeFactory scopes,
    IOptions<ResearchOptions> options,
    TimeProvider clock,
    ILogger<Worker> log) : BackgroundService
{
    private const string Strategy = "dip";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        log.LogInformation("Research starting: screening {Count} assets every {Minutes} min", o.Watchlist.Count, o.CycleMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(o.CycleMinutes);
            try
            {
                using var scope = scopes.CreateScope();
                await ResearchCycleAsync(
                    scope.ServiceProvider.GetRequiredService<KrakenPublicClient>(),
                    scope.ServiceProvider.GetRequiredService<CoppertopApiClient>(),
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

    private async Task ResearchCycleAsync(KrakenPublicClient kraken, CoppertopApiClient api, ResearchOptions o, CancellationToken ct)
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

        var ranked = Screener.Rank(results, o.MaxOpportunities);
        var expires = clock.GetUtcNow().AddHours(o.OpportunityLifetimeHours);
        var published = 0;

        foreach (var r in ranked.Where(r => r.Approved))
        {
            var opp = await api.PublishOpportunityAsync(new CreateOpportunityDto(
                r.Asset, Strategy, r.Metrics!.LastPrice, o.TakeProfitPct, o.StopLossPct, o.MaxSpendUsd,
                r.Confidence, r.Reason, expires), ct);
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
}
