using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Coppertop.Trader.Kraken;

public sealed class KrakenPairCache
{
    public System.Collections.Concurrent.ConcurrentDictionary<string, PairInfo> ByAlt { get; } = new(StringComparer.OrdinalIgnoreCase);
    public System.Collections.Concurrent.ConcurrentDictionary<string, PairInfo> ByKey { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class KrakenClient
{
    private static long _lastNonce;

    private readonly HttpClient _http;
    private readonly KrakenOptions _options;
    private readonly KrakenPairCache _pairs;

    public KrakenClient(HttpClient http, IOptions<KrakenOptions> options, KrakenPairCache pairs)
    {
        _http = http;
        _options = options.Value;
        _pairs = pairs;
        _http.BaseAddress ??= new Uri(_options.BaseUrl);
    }

    public bool HasCredentials => _options.HasCredentials;

    public async Task<IReadOnlyDictionary<string, PairInfo>> EnsurePairsAsync(IEnumerable<string> altNames, CancellationToken ct)
    {
        var missing = altNames.Where(a => !_pairs.ByAlt.ContainsKey(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count > 0)
        {
            var result = await GetPublicAsync($"/0/public/AssetPairs?pair={string.Join(',', missing)}", ct);
            foreach (var prop in result.EnumerateObject())
            {
                var v = prop.Value;
                var info = new PairInfo(
                    prop.Name,
                    v.GetProperty("altname").GetString()!,
                    v.GetProperty("pair_decimals").GetInt32(),
                    v.GetProperty("lot_decimals").GetInt32(),
                    ParseDecimal(v, "ordermin"),
                    ParseDecimal(v, "costmin"));
                _pairs.ByAlt[info.AltName] = info;
                _pairs.ByKey[info.Key] = info;
            }
        }
        return _pairs.ByAlt;
    }

    public async Task<IReadOnlyDictionary<string, Ticker>> GetTickersAsync(IReadOnlyCollection<string> altNames, CancellationToken ct)
    {
        if (altNames.Count == 0) return new Dictionary<string, Ticker>();
        await EnsurePairsAsync(altNames, ct);

        var result = await GetPublicAsync($"/0/public/Ticker?pair={string.Join(',', altNames)}", ct);
        var tickers = new Dictionary<string, Ticker>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in result.EnumerateObject())
        {
            var alt = _pairs.ByKey.TryGetValue(prop.Name, out var info) ? info.AltName : prop.Name;
            tickers[alt] = new Ticker(
                alt,
                ParseDecimal(prop.Value.GetProperty("a")[0]),
                ParseDecimal(prop.Value.GetProperty("b")[0]),
                ParseDecimal(prop.Value.GetProperty("c")[0]));
        }
        return tickers;
    }

    public async Task<IReadOnlyList<Candle>> GetCandlesAsync(string altName, int intervalMinutes, CancellationToken ct)
    {
        var result = await GetPublicAsync($"/0/public/OHLC?pair={altName}&interval={intervalMinutes}", ct);
        var candles = new List<Candle>();
        foreach (var prop in result.EnumerateObject())
        {
            if (prop.Name == "last" || prop.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in prop.Value.EnumerateArray())
            {
                candles.Add(new Candle(
                    DateTimeOffset.FromUnixTimeSeconds(row[0].GetInt64()),
                    ParseDecimal(row[1]), ParseDecimal(row[2]), ParseDecimal(row[3]),
                    ParseDecimal(row[4]), ParseDecimal(row[6])));
            }
        }
        return candles;
    }

    public async Task<IReadOnlyDictionary<string, decimal>> GetBalancesAsync(CancellationToken ct)
    {
        var result = await PostPrivateAsync("/0/private/Balance", [], ct);
        return result.EnumerateObject().ToDictionary(p => p.Name, p => ParseDecimal(p.Value));
    }

    public async Task<AddOrderResult> AddOrderAsync(AddOrderRequest order, PairInfo pair, CancellationToken ct)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("ordertype", order.OrderType),
            new("type", order.Side),
            new("volume", Format(order.Volume, pair.LotDecimals)),
            new("pair", pair.AltName),
            new("price", Format(order.Price, pair.PriceDecimals)),
        };
        if (order.PostOnly) fields.Add(new("oflags", "post"));
        if (order.CloseLimitPrice is { } closePrice)
        {
            // Kraken conditional close: once the entry fills, Kraken places this limit sell itself.
            fields.Add(new("close[ordertype]", "limit"));
            fields.Add(new("close[price]", Format(closePrice, pair.PriceDecimals)));
        }
        if (order.ValidateOnly) fields.Add(new("validate", "true"));

        var result = await PostPrivateAsync("/0/private/AddOrder", fields, ct);
        var txIds = result.TryGetProperty("txid", out var tx)
            ? tx.EnumerateArray().Select(t => t.GetString()!).ToList()
            : [];
        var descr = result.GetProperty("descr");
        return new AddOrderResult(
            txIds,
            descr.TryGetProperty("order", out var o) ? o.GetString() ?? "" : "",
            descr.TryGetProperty("close", out var cl) ? cl.GetString() : null);
    }

    public static string Format(decimal value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.ToZero).ToString("F" + decimals, CultureInfo.InvariantCulture);

    private async Task<JsonElement> GetPublicAsync(string pathAndQuery, CancellationToken ct)
    {
        using var response = await _http.GetAsync(pathAndQuery, ct);
        return await ReadResultAsync(response, ct);
    }

    private async Task<JsonElement> PostPrivateAsync(string path, List<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        if (!_options.HasCredentials)
            throw new KrakenException("Kraken API credentials are not configured.");

        var nonce = NextNonce().ToString(CultureInfo.InvariantCulture);
        fields.Insert(0, new("nonce", nonce));
        // The signed string must be byte-identical to the body we send.
        var postData = string.Join('&', fields.Select(f => $"{Uri.EscapeDataString(f.Key)}={Uri.EscapeDataString(f.Value)}"));

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded")
        };
        request.Headers.Add("API-Key", _options.ApiKey);
        request.Headers.Add("API-Sign", KrakenSigner.Sign(path, nonce, postData, _options.ApiSecret));

        using var response = await _http.SendAsync(request, ct);
        return await ReadResultAsync(response, ct);
    }

    private static async Task<JsonElement> ReadResultAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new KrakenException($"Kraken HTTP {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("error", out var errors) && errors.GetArrayLength() > 0)
            throw new KrakenException("Kraken error: " + string.Join("; ", errors.EnumerateArray().Select(e => e.GetString())));

        return root.GetProperty("result").Clone();
    }

    private static long NextNonce()
    {
        while (true)
        {
            var last = Interlocked.Read(ref _lastNonce);
            var next = Math.Max(last + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
            if (Interlocked.CompareExchange(ref _lastNonce, next, last) == last) return next;
        }
    }

    private static decimal ParseDecimal(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var v) ? ParseDecimal(v) : 0m;

    private static decimal ParseDecimal(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDecimal(),
        JsonValueKind.String => decimal.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => 0m
    };
}
