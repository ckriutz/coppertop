using System.Globalization;
using System.Text.Json;

namespace Coppertop.Research.Kraken;

public sealed record Ticker(string AltName, decimal Ask, decimal Bid, decimal Last, decimal Volume24h, decimal Vwap24h)
{
    public decimal SpreadPct => Ask > 0 ? (Ask - Bid) / Ask * 100m : 0m;
    public decimal Volume24hUsd => Volume24h * Vwap24h;
}

public sealed record Candle(DateTimeOffset Time, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume);

// Research's own minimal, public-only Kraken client (no keys, cannot trade).
public sealed class KrakenPublicClient
{
    private readonly HttpClient _http;

    public KrakenPublicClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri("https://api.kraken.com");
    }

    /// <summary>Tickers keyed by pair alt name (e.g. "XBTUSD"). Kraken answers with its own pair keys, so map them back.</summary>
    public async Task<IReadOnlyDictionary<string, Ticker>> GetTickersAsync(IReadOnlyCollection<string> altNames, CancellationToken ct)
    {
        var joined = string.Join(',', altNames);
        var pairs = await GetResultAsync($"/0/public/AssetPairs?pair={joined}", ct);
        var keyToAlt = pairs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("altname").GetString()!);
        return ParseTickers(await GetResultAsync($"/0/public/Ticker?pair={joined}", ct), keyToAlt);
    }

    public static IReadOnlyDictionary<string, Ticker> ParseTickers(JsonElement result, IReadOnlyDictionary<string, string> keyToAlt)
    {
        var tickers = new Dictionary<string, Ticker>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in result.EnumerateObject())
        {
            var alt = keyToAlt.GetValueOrDefault(t.Name, t.Name);
            var v = t.Value;
            tickers[alt] = new Ticker(alt,
                Dec(v.GetProperty("a")[0]), Dec(v.GetProperty("b")[0]), Dec(v.GetProperty("c")[0]),
                Dec(v.GetProperty("v")[1]), Dec(v.GetProperty("p")[1]));
        }
        return tickers;
    }

    /// <summary>Up to 720 OHLC candles, oldest first. The last one is still forming.</summary>
    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string altName, int intervalMinutes, CancellationToken ct) =>
        ParseCandles(await GetResultAsync($"/0/public/OHLC?pair={altName}&interval={intervalMinutes}", ct));

    public static IReadOnlyList<Candle> ParseCandles(JsonElement result)
    {
        var candles = new List<Candle>();
        foreach (var prop in result.EnumerateObject())
        {
            if (prop.Name == "last" || prop.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in prop.Value.EnumerateArray())
            {
                candles.Add(new Candle(
                    DateTimeOffset.FromUnixTimeSeconds(row[0].GetInt64()),
                    Dec(row[1]), Dec(row[2]), Dec(row[3]), Dec(row[4]), Dec(row[6])));
            }
        }
        return candles;
    }

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.Number
        ? e.GetDecimal()
        : decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);

    private async Task<JsonElement> GetResultAsync(string path, CancellationToken ct)
    {
        using var res = await _http.GetAsync(path, ct);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var errors = doc.RootElement.GetProperty("error");
        if (errors.GetArrayLength() > 0)
            throw new InvalidOperationException("Kraken error: " + string.Join("; ", errors.EnumerateArray().Select(e => e.GetString())));
        return doc.RootElement.GetProperty("result").Clone();
    }
}
