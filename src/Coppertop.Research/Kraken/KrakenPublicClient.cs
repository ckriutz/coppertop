using System.Globalization;
using System.Text.Json;

namespace Coppertop.Research.Kraken;

// Research's own minimal, public-only Kraken client (no keys, cannot trade).
public sealed class KrakenPublicClient
{
    private readonly HttpClient _http;

    public KrakenPublicClient(HttpClient http)
    {
        _http = http;
        _http.BaseAddress ??= new Uri("https://api.kraken.com");
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetLastPricesAsync(IReadOnlyCollection<string> altNames, CancellationToken ct)
    {
        var joined = string.Join(',', altNames);
        var pairs = await GetResultAsync($"/0/public/AssetPairs?pair={joined}", ct);
        var keyToAlt = pairs.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetProperty("altname").GetString()!);

        var tickers = await GetResultAsync($"/0/public/Ticker?pair={joined}", ct);
        var prices = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tickers.EnumerateObject())
        {
            var alt = keyToAlt.GetValueOrDefault(t.Name, t.Name);
            prices[alt] = decimal.Parse(t.Value.GetProperty("c")[0].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        return prices;
    }

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
