using Coppertop.Research.Api;
using Coppertop.Research.Kraken;
using Microsoft.Extensions.Options;

namespace Coppertop.Research;

/// <summary>
/// STUB research: approves every watchlist asset for dip-buying below its current price.
/// No LLM / TypeSafe / Sonar yet – that replaces <see cref="ResearchCycleAsync"/> in a later step.
/// </summary>
public sealed class Worker(
    IServiceScopeFactory scopes,
    IOptions<ResearchOptions> options,
    TimeProvider clock,
    ILogger<Worker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        log.LogInformation("Research (stub) starting: {Count} assets, every {Minutes} min", o.Watchlist.Count, o.CycleMinutes);

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

        var prices = await kraken.GetLastPricesAsync(o.Watchlist, ct);
        var expires = clock.GetUtcNow().AddHours(o.OpportunityLifetimeHours);

        foreach (var asset in o.Watchlist)
        {
            if (!prices.TryGetValue(asset, out var last))
            {
                log.LogWarning("{Asset}: no price from Kraken", asset);
                continue;
            }

            var published = await api.PublishOpportunityAsync(new CreateOpportunityDto(
                asset, "dip", last, o.TakeProfitPct, o.StopLossPct, o.MaxSpendUsd,
                Confidence: 0.5m,
                Reason: $"stub: approve dip-buy below last price {last}",
                ExpiresAt: expires), ct);

            if (published is null)
                log.LogInformation("{Asset}: vetoed, not published", asset);
            else
                log.LogInformation("{Asset}: opportunity {Id} published, max entry {Max}, expires {Expires:u}",
                    asset, published.Id, published.MaxEntryPrice, published.ExpiresAt);
        }
    }
}
