using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Coppertop.Research.Api;

// Research's own view of the Coppertop API contract. Intentionally not shared with other services.
public sealed record CreateOpportunityDto(
    string Asset,
    string Strategy,
    decimal MaxEntryPrice,
    decimal TakeProfitPct,
    decimal StopLossPct,
    decimal MaxSpendUsd,
    decimal Confidence,
    string Reason,
    DateTimeOffset ExpiresAt,
    object? Context = null);

public sealed record OpportunityDto(long Id, string Asset, string Strategy, decimal MaxEntryPrice, DateTimeOffset ExpiresAt);

public sealed record ScreenResultDto(string Asset, bool Approved, decimal Confidence, string Reason, object? Metrics);

public sealed record PublishScreenDto(IReadOnlyList<ScreenResultDto> Results);

public sealed record CreateVetoDto(string Asset, string Reason, DateTimeOffset ExpiresAt);

public sealed record VetoDto(long Id, string Asset, string Reason, DateTimeOffset ExpiresAt);

public sealed record TokenUsageDto(long Id, string Service, decimal CostUsd, DateTimeOffset CreatedAt);

public sealed record CreateTokenUsageDto(string Service, string Model, string Purpose, long InputTokens, long OutputTokens, decimal CostUsd);

public sealed class CoppertopApiClient
{
    private readonly HttpClient _http;

    public CoppertopApiClient(HttpClient http, IOptions<CoppertopApiOptions> options)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(options.Value.BaseUrl);
        if (!string.IsNullOrEmpty(options.Value.ApiKey))
            _http.DefaultRequestHeaders.Add("X-Api-Key", options.Value.ApiKey);
    }

    // Returns null when the asset is currently vetoed (409).
    public async Task<OpportunityDto?> PublishOpportunityAsync(CreateOpportunityDto dto, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("/opportunities", dto, ct);
        if (res.StatusCode == HttpStatusCode.Conflict) return null;
        await EnsureSuccessAsync(res, ct);
        return await res.Content.ReadFromJsonAsync<OpportunityDto>(ct);
    }

    public async Task<IReadOnlyList<OpportunityDto>> GetActiveOpportunitiesAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<List<OpportunityDto>>("/opportunities?active=true", ct) ?? [];

    public async Task CancelOpportunityAsync(long id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"/opportunities/{id}/cancel", null, ct);
        if (res.StatusCode == HttpStatusCode.Conflict) return; // already consumed/expired
        await EnsureSuccessAsync(res, ct);
    }

    public async Task PublishScreenAsync(PublishScreenDto dto, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("/research/screen", dto, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task PublishVetoAsync(CreateVetoDto dto, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("/vetoes", dto, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<IReadOnlyList<VetoDto>> GetActiveVetoesAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<List<VetoDto>>("/vetoes?active=true", ct) ?? [];

    // The Api returns the latest 500 rows, which covers a day of news checks many times over.
    public async Task<IReadOnlyList<TokenUsageDto>> GetTokenUsageAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<List<TokenUsageDto>>("/token-usage", ct) ?? [];

    public async Task RecordTokenUsageAsync(CreateTokenUsageDto dto, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("/token-usage", dto, ct);
        await EnsureSuccessAsync(res, ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Coppertop API {(int)res.StatusCode}: {body}", null, res.StatusCode);
    }
}
