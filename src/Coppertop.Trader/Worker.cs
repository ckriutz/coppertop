using Coppertop.Trader.Trading;
using Microsoft.Extensions.Options;

namespace Coppertop.Trader;

public sealed class Worker(IServiceScopeFactory scopes, IOptions<TraderOptions> options, ILogger<Worker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        log.LogInformation("Trader starting in {Mode} mode, cycle every {Seconds}s", o.Mode, o.CycleSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(o.CycleSeconds));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<TraderEngine>().RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One bad cycle (Kraken hiccup, bad data) must not kill the trader.
                log.LogError(ex, "Trading cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
