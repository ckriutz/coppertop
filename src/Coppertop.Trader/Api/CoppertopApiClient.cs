using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Coppertop.Trader.Api;

// Trader's own view of the Coppertop API contract. Intentionally not shared with other services.
public sealed record OpportunityDto(
    long Id,
    string Asset,
    string Strategy,
    decimal MaxEntryPrice,
    decimal TakeProfitPct,
    decimal StopLossPct,
    decimal MaxSpendUsd,
    decimal Confidence,
    string Reason,
    DateTimeOffset ExpiresAt);

public sealed record PositionDto(
    long Id,
    long? OpportunityId,
    string Asset,
    decimal Volume,
    decimal EntryPrice,
    decimal EntryFeeUsd,
    decimal CostUsd,
    decimal TakeProfitPrice,
    decimal StopLossPrice,
    string Status,
    bool IsSimulated);

public sealed record OpenPositionDto(
    long? OpportunityId,
    string Asset,
    decimal Volume,
    decimal EntryPrice,
    decimal EntryFeeUsd,
    decimal TakeProfitPrice,
    decimal StopLossPrice,
    bool IsSimulated);

public sealed record ClosePositionDto(decimal ExitPrice, decimal ExitFeeUsd, string Reason);

public sealed record CreateOrderDto(
    long? OpportunityId,
    long? PositionId,
    string Asset,
    string Side,
    string OrderType,
    string Purpose,
    decimal Price,
    decimal Volume,
    string Status,
    string? KrakenTxId,
    bool IsSimulated,
    string? Note);

public sealed record OrderDto(long Id, string Status);

public sealed record CreateTradeDto(
    long? OrderId,
    long? PositionId,
    string Asset,
    string Side,
    decimal Price,
    decimal Volume,
    decimal FeeUsd,
    bool IsSimulated,
    DateTimeOffset? ExecutedAt);

public sealed record SummaryDto(decimal RealizedPnlUsd, decimal OpenExposureUsd, int OpenPositions);

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

    public async Task<IReadOnlyList<OpportunityDto>> GetActiveOpportunitiesAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<List<OpportunityDto>>("/opportunities?active=true", ct) ?? [];

    // Returns false if another consumer already took it (409) or it is no longer active.
    public async Task<bool> TryConsumeOpportunityAsync(long id, CancellationToken ct)
    {
        using var res = await _http.PostAsync($"/opportunities/{id}/consume", null, ct);
        if (res.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        res.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<IReadOnlyList<PositionDto>> GetOpenPositionsAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<List<PositionDto>>("/positions?status=open", ct) ?? [];

    public Task<PositionDto> OpenPositionAsync(OpenPositionDto dto, CancellationToken ct) =>
        PostAsync<OpenPositionDto, PositionDto>("/positions", dto, ct);

    public Task<PositionDto> ClosePositionAsync(long id, ClosePositionDto dto, CancellationToken ct) =>
        PostAsync<ClosePositionDto, PositionDto>($"/positions/{id}/close", dto, ct);

    public Task<OrderDto> CreateOrderAsync(CreateOrderDto dto, CancellationToken ct) =>
        PostAsync<CreateOrderDto, OrderDto>("/orders", dto, ct);

    public async Task CreateTradeAsync(CreateTradeDto dto, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync("/trades", dto, ct);
        await EnsureSuccessAsync(res, ct);
    }

    public async Task<SummaryDto> GetSummaryAsync(CancellationToken ct) =>
        await _http.GetFromJsonAsync<SummaryDto>("/summary", ct)
        ?? throw new InvalidOperationException("Empty summary response.");

    private async Task<TOut> PostAsync<TIn, TOut>(string path, TIn body, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(path, body, ct);
        await EnsureSuccessAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<TOut>(ct))!;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException($"Coppertop API {(int)res.StatusCode}: {body}", null, res.StatusCode);
    }
}
