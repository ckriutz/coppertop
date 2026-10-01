using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Coppertop.Research.News;

public sealed record NewsVerdict(bool Block, string Reason);

public sealed record SonarResult(NewsVerdict Verdict, string Model, long InputTokens, long OutputTokens, decimal CostUsd, IReadOnlyList<string> Sources);

/// <summary>
/// Asks Perplexity Sonar (through OpenRouter) whether there is recent, specific bad news about a coin.
/// Sonar searches the web itself, so one call per coin is enough.
/// </summary>
public sealed class SonarClient
{
    private readonly HttpClient _http;
    private readonly OpenRouterOptions _o;

    public SonarClient(HttpClient http, IOptions<OpenRouterOptions> options)
    {
        _http = http;
        _o = options.Value;
        _http.BaseAddress ??= new Uri(_o.BaseUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(_o.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _o.ApiKey);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "Coppertop");
    }

    public bool Enabled => !string.IsNullOrWhiteSpace(_o.ApiKey);

    // A news reply is ~200 prompt + ~60 completion tokens; the search fee dominates.
    public decimal EstimatedCallCostUsd => 500m * (_o.InputUsdPerMillion + _o.OutputUsdPerMillion) / 1_000_000m + _o.SearchUsdPerRequest;

    public async Task<SonarResult> CheckNewsAsync(string asset, int lookbackHours, CancellationToken ct)
    {
        var body = new
        {
            model = _o.NewsModel,
            temperature = 0,
            max_tokens = 200,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = UserPrompt(asset, lookbackHours) },
            },
            web_search_options = new { search_context_size = "low" },
            usage = new { include = true },
        };

        using var res = await _http.PostAsJsonAsync("chat/completions", body, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenRouter {(int)res.StatusCode}: {Trim(json, 300)}");
        return ParseResponse(json, _o);
    }

    public const string SystemPrompt =
        "You are a news risk filter for a small crypto trading bot that holds positions for minutes to hours. " +
        "You only flag severe, specific, recent negative events. Reply with JSON only, no prose, no markdown.";

    public static string UserPrompt(string asset, int lookbackHours) =>
        $$"""
        Search news from the last {{lookbackHours}} hours about {{CoinName(asset)}}.
        Is there a specific negative event about this coin itself that makes buying it in the next few hours risky?
        Block only for an event that directly involves this coin: a hack or exploit of its own network, protocol or token; its delisting
        or a trading halt for it (especially on Kraken); regulatory or legal action aimed at it; its network going down or halting;
        a large unlock, insider or whale dump of it; a depeg; collapse of its team or project.
        Do NOT block for: hacks or outages at other exchanges, wallets or protocols, even if they hold this coin; news about other coins;
        general market weakness, ETF flows, price moves, commentary, predictions or opinion pieces. When unsure, allow.
        Reply exactly: {"verdict":"allow" or "block","reason":"one short sentence"}
        """;

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XBT"] = "Bitcoin (BTC)", ["BTC"] = "Bitcoin (BTC)", ["ETH"] = "Ethereum (ETH)", ["SOL"] = "Solana (SOL)",
        ["XRP"] = "XRP", ["ADA"] = "Cardano (ADA)", ["XDG"] = "Dogecoin (DOGE)", ["DOGE"] = "Dogecoin (DOGE)",
        ["DOT"] = "Polkadot (DOT)", ["LINK"] = "Chainlink (LINK)", ["LTC"] = "Litecoin (LTC)",
        ["AVAX"] = "Avalanche (AVAX)", ["XLM"] = "Stellar (XLM)", ["BCH"] = "Bitcoin Cash (BCH)",
    };

    /// <summary>"XBTUSD" → "Bitcoin (BTC)"; unknown coins fall back to the base symbol.</summary>
    public static string CoinName(string asset)
    {
        var b = asset.EndsWith("USD", StringComparison.OrdinalIgnoreCase) ? asset[..^3] : asset;
        return Names.TryGetValue(b, out var n) ? n : $"the {b.ToUpperInvariant()} cryptocurrency";
    }

    public static SonarResult ParseResponse(string json, OpenRouterOptions o)
    {
        var root = JsonNode.Parse(json) ?? throw new InvalidOperationException("empty OpenRouter response");
        if (root["error"] is JsonNode err)
            throw new InvalidOperationException($"OpenRouter error: {Trim(err["message"]?.ToString() ?? err.ToJsonString(), 300)}");

        var message = root["choices"]?[0]?["message"];
        var content = message?["content"]?.ToString() ?? throw new InvalidOperationException("no content in OpenRouter response");
        var verdict = ParseVerdict(content);

        var usage = root["usage"];
        var input = Long(usage?["prompt_tokens"]);
        var output = Long(usage?["completion_tokens"]);
        // OpenRouter reports the real charge (tokens + search fee) when usage accounting is on; otherwise estimate it.
        var cost = usage?["cost"] is JsonValue c && c.TryGetValue<decimal>(out var reported)
            ? reported
            : (input * o.InputUsdPerMillion + output * o.OutputUsdPerMillion) / 1_000_000m + o.SearchUsdPerRequest;

        var sources = new List<string>();
        if (root["citations"] is JsonArray cites)
            sources.AddRange(cites.Select(x => x?.ToString()).OfType<string>());
        if (message?["annotations"] is JsonArray anns)
            sources.AddRange(anns.Select(a => a?["url_citation"]?["url"]?.ToString()).OfType<string>());

        return new SonarResult(verdict, root["model"]?.ToString() ?? o.NewsModel, input, output, cost,
            sources.Distinct().Take(5).ToList());
    }

    /// <summary>Reads {"verdict":"allow|block","reason":"..."}, tolerating code fences and text around it.</summary>
    public static NewsVerdict ParseVerdict(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidOperationException($"no JSON in Sonar reply: {Trim(content, 200)}");

        using var doc = JsonDocument.Parse(content[start..(end + 1)]);
        var v = doc.RootElement.TryGetProperty("verdict", out var ve) ? ve.GetString()?.Trim().ToLowerInvariant() : null;
        var reason = doc.RootElement.TryGetProperty("reason", out var re) ? re.GetString()?.Trim() : null;
        reason = string.IsNullOrWhiteSpace(reason) ? "no reason given" : Trim(Regex.Replace(reason, @"\[\d+\]", "").Trim(), 240);
        return v switch
        {
            "block" => new NewsVerdict(true, reason),
            "allow" => new NewsVerdict(false, reason),
            _ => throw new InvalidOperationException($"unexpected Sonar verdict '{v}'"),
        };
    }

    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
