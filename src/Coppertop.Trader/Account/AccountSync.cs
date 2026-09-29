using Coppertop.Trader.Api;
using Coppertop.Trader.Kraken;
using Microsoft.Extensions.Options;

namespace Coppertop.Trader.Account;

/// <summary>Values Kraken balances in USD. Pure, no I/O.</summary>
public static class AccountValuation
{
    private static readonly HashSet<string> UsdKeys = new(StringComparer.OrdinalIgnoreCase) { "ZUSD", "USD" };

    /// <summary>
    /// Kraken asset codes can carry a suffix for earn/staking/holds (e.g. "USD.HOLD", "DOT.S", "XBT.F").
    /// The part before the dot is the asset used for pricing.
    /// </summary>
    public static (string Base, string Suffix) Split(string asset)
    {
        var dot = asset.IndexOf('.');
        return dot < 0 ? (asset, "") : (asset[..dot], asset[dot..]);
    }

    /// <summary>Kraken base asset key → its USD-quoted pair (e.g. "XXBT" → XBTUSD).</summary>
    public static Dictionary<string, PairInfo> UsdPairsByBase(IEnumerable<PairInfo> pairs) =>
        pairs.Where(p => UsdKeys.Contains(p.Quote) && !p.Key.EndsWith(".d", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Base, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.AltName.Length).First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dust = a balance Kraken won't let us sell: below the pair's minimum order size (ordermin)
    /// or worth less than its minimum order value (costmin) at the bid.
    /// </summary>
    public static bool IsDust(decimal balance, PairInfo pair, decimal bid) =>
        balance < pair.OrderMin || balance * bid < pair.CostMin;

    /// <summary>Alt names of the USD pairs needed to price these balances (skips zero balances and USD itself).</summary>
    public static IReadOnlyList<string> PairsToPrice(IEnumerable<KrakenBalance> balances, IReadOnlyDictionary<string, PairInfo> usdPairs) =>
        balances.Where(b => b.Balance != 0)
            .Select(b => Split(b.Asset).Base)
            .Where(b => !UsdKeys.Contains(b) && usdPairs.ContainsKey(b))
            .Select(b => usdPairs[b].AltName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Non-zero balances priced at the bid (what we could sell for). Unknown assets keep a null price and are never
    /// called dust, because we can't tell. USD is never dust; it's cash we can spend.
    /// </summary>
    public static IReadOnlyList<AccountBalanceDto> Value(
        IEnumerable<KrakenBalance> balances,
        IReadOnlyDictionary<string, PairInfo> usdPairs,
        IReadOnlyDictionary<string, Ticker> tickers)
    {
        var result = new List<AccountBalanceDto>();
        foreach (var b in balances.Where(b => b.Balance != 0))
        {
            var (baseKey, suffix) = Split(b.Asset);
            if (UsdKeys.Contains(baseKey))
            {
                result.Add(new AccountBalanceDto(b.Asset, "USD" + suffix, b.Balance, b.HoldTrade, 1m));
                continue;
            }

            decimal? price = null;
            var dust = false;
            var display = baseKey;
            if (usdPairs.TryGetValue(baseKey, out var pair))
            {
                var alt = pair.AltName;
                display = alt.EndsWith("USD", StringComparison.OrdinalIgnoreCase) ? alt[..^3] : alt;
                if (tickers.TryGetValue(alt, out var t))
                {
                    price = t.Bid;
                    dust = IsDust(b.Balance, pair, t.Bid);
                }
            }
            result.Add(new AccountBalanceDto(b.Asset, display + suffix, b.Balance, b.HoldTrade, price, dust));
        }
        return result;
    }
}

/// <summary>
/// Reads the real Kraken account (private BalanceEx) on its own timer and posts a snapshot to the Api.
/// Read-only: it never places orders. Runs only when Kraken credentials are configured.
/// </summary>
public sealed class AccountWorker(
    IServiceScopeFactory scopes,
    IOptions<KrakenOptions> kraken,
    ILogger<AccountWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = kraken.Value;
        if (!o.HasCredentials)
        {
            log.LogInformation("No Kraken credentials; account balances won't be synced.");
            return;
        }

        log.LogInformation("Syncing Kraken account balances every {Seconds}s", o.AccountRefreshSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(15, o.AccountRefreshSeconds)));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await SyncAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Account sync failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task SyncAsync(IServiceProvider services, CancellationToken ct)
    {
        var client = services.GetRequiredService<KrakenClient>();
        var api = services.GetRequiredService<CoppertopApiClient>();

        IReadOnlyList<KrakenBalance> balances;
        try
        {
            balances = await client.GetBalancesAsync(ct);
        }
        catch (KrakenException ex)
        {
            // e.g. "EGeneral:Permission denied" when the key lacks "Query Funds". Keep the last good snapshot.
            log.LogWarning("Kraken balance request failed: {Message}", ex.Message);
            await api.PostAccountAsync(new AccountSnapshotDto(null, ex.Message), ct);
            return;
        }

        var usdPairs = AccountValuation.UsdPairsByBase(await client.GetAllPairsAsync(ct));
        var toPrice = AccountValuation.PairsToPrice(balances, usdPairs);
        var tickers = await client.GetTickersAsync(toPrice, ct);
        var valued = AccountValuation.Value(balances, usdPairs, tickers);

        await api.PostAccountAsync(new AccountSnapshotDto(valued, null), ct);
        log.LogInformation("Kraken account: {Count} non-zero balance(s), ≈${Total:F2}",
            valued.Count, valued.Sum(b => b.Balance * (b.PriceUsd ?? 0m)));
    }
}
