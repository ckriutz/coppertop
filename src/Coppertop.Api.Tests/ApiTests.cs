using System.Net;
using System.Net.Http.Json;
using Coppertop.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Coppertop.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"coppertop-test-{Guid.NewGuid():N}.db");

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    public string? ApiKey { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Coppertop", $"Data Source={_dbPath};Pooling=False");
        builder.UseSetting("Api:Key", ApiKey ?? "");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var f in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}

public sealed class ApiTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public ApiTests() => _client = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private CreateOpportunityRequest Opp(string asset = "XBTUSD", decimal confidence = 0.6m) => new(
        asset, "dip", 60000m, 1.5m, 2.0m, 12m, confidence, "test", _factory.Clock.GetUtcNow().AddHours(4));

    private async Task<Opportunity> CreateOpp(CreateOpportunityRequest req)
    {
        var res = await _client.PostAsJsonAsync("/opportunities", req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<Opportunity>())!;
    }

    private async Task<List<Opportunity>> Active() =>
        (await _client.GetFromJsonAsync<List<Opportunity>>("/opportunities?active=true"))!;

    [Fact]
    public async Task CreatedOpportunity_IsActive_AndAssetNormalized()
    {
        var created = await CreateOpp(Opp(" xbtusd "));

        Assert.Equal("XBTUSD", created.Asset);
        Assert.Equal(OpportunityStatus.Active, created.Status);
        Assert.Single(await Active());
    }

    [Fact]
    public async Task NewOpportunity_SupersedesPreviousForSameAssetAndStrategy()
    {
        var first = await CreateOpp(Opp());
        var second = await CreateOpp(Opp());

        var active = await Active();
        Assert.Single(active);
        Assert.Equal(second.Id, active[0].Id);
        var old = await _client.GetFromJsonAsync<Opportunity>($"/opportunities/{first.Id}");
        Assert.Equal(OpportunityStatus.Superseded, old!.Status);
    }

    [Fact]
    public async Task InvalidOpportunity_Returns400()
    {
        var res = await _client.PostAsJsonAsync("/opportunities", Opp() with { TakeProfitPct = 0, Confidence = 2 });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Veto_CancelsActiveOpportunities_AndBlocksNewOnes()
    {
        await CreateOpp(Opp("ETHUSD"));
        await CreateOpp(Opp("XBTUSD"));

        var veto = await _client.PostAsJsonAsync("/vetoes",
            new CreateVetoRequest("ETHUSD", "exchange hack news", _factory.Clock.GetUtcNow().AddHours(12)));
        Assert.Equal(HttpStatusCode.Created, veto.StatusCode);

        var active = await Active();
        Assert.Equal(["XBTUSD"], active.Select(o => o.Asset));

        var blocked = await _client.PostAsJsonAsync("/opportunities", Opp("ETHUSD"));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        _factory.Clock.Advance(TimeSpan.FromHours(13));
        await CreateOpp(Opp("ETHUSD"));
    }

    [Fact]
    public async Task Opportunity_CanOnlyBeConsumedOnce()
    {
        var opp = await CreateOpp(Opp());

        var first = await _client.PostAsync($"/opportunities/{opp.Id}/consume", null);
        var second = await _client.PostAsync($"/opportunities/{opp.Id}/consume", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Empty(await Active());
    }

    [Fact]
    public async Task ExpiredOpportunities_AreNotActive()
    {
        await CreateOpp(Opp());
        _factory.Clock.Advance(TimeSpan.FromHours(5));

        Assert.Empty(await Active());
    }

    [Fact]
    public async Task ClosingPosition_ComputesPnl_AndSummaryIncludesTokenCost()
    {
        var open = await _client.PostAsJsonAsync("/positions",
            new OpenPositionRequest(null, "XBTUSD", 0.0002m, 50000m, 0.025m, 50750m, 49000m, true));
        Assert.Equal(HttpStatusCode.Created, open.StatusCode);
        var position = (await open.Content.ReadFromJsonAsync<Position>())!;
        Assert.Equal(10.025m, position.CostUsd);

        var close = await _client.PostAsJsonAsync($"/positions/{position.Id}/close",
            new ClosePositionRequest(50750m, 0.02538m, "take_profit"));
        var closed = (await close.Content.ReadFromJsonAsync<Position>())!;
        // 0.0002 * 50750 = 10.15; 10.15 - 0.02538 - 10.025 = 0.09962
        Assert.Equal(0.09962m, closed.RealizedPnlUsd!.Value, 5);

        var again = await _client.PostAsJsonAsync($"/positions/{position.Id}/close",
            new ClosePositionRequest(50750m, 0m, "dup"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        await _client.PostAsJsonAsync("/token-usage",
            new CreateTokenUsageRequest("research", "muse", "scan", 1000, 200, 0.01m));

        var summary = (await _client.GetFromJsonAsync<Summary>("/summary"))!;
        Assert.Equal(1, summary.ClosedPositions);
        Assert.Equal(1, summary.WinningPositions);
        Assert.Equal(0.01m, summary.TokenCostUsd, 5);
        Assert.Equal(0.08962m, summary.NetAfterTokensUsd, 5);
        Assert.Equal(0, summary.OpenPositions);
    }

    [Fact]
    public async Task Orders_CanBeCreatedAndUpdated()
    {
        var res = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest(
            null, null, "XBTUSD", "buy", "limit", "entry", 50000m, 0.0002m, "validated", null, true, "validate-only"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var order = (await res.Content.ReadFromJsonAsync<Order>())!;
        Assert.True(order.IsSimulated);

        var patch = await _client.PatchAsJsonAsync($"/orders/{order.Id}", new UpdateOrderRequest("filled", "OABC-123", null));
        var updated = (await patch.Content.ReadFromJsonAsync<Order>())!;
        Assert.Equal("filled", updated.Status);
        Assert.Equal("OABC-123", updated.KrakenTxId);
        Assert.Equal("validate-only", updated.Note);
    }

    private async Task<Order> CreateOrder(string side, string purpose, long? positionId = null, decimal price = 50000m)
    {
        var res = await _client.PostAsJsonAsync("/orders", new CreateOrderRequest(
            null, positionId, "XBTUSD", side, "limit", purpose, price, 0.0002m, "open", null, true, null));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<Order>())!;
    }

    [Fact]
    public async Task FillingEntryOrder_OpensPosition_RecordsTrade_AndLinksOrder()
    {
        var order = await CreateOrder("buy", "entry");
        var executedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var res = await _client.PostAsJsonAsync($"/orders/{order.Id}/fill",
            new FillOrderRequest(50000m, 0.0002m, 0.025m, executedAt, 50750m, 49000m, "paper fill"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var fill = (await res.Content.ReadFromJsonAsync<FillOrderResponse>())!;

        Assert.Equal("filled", fill.Order.Status);
        Assert.Equal(fill.Position.Id, fill.Order.PositionId);
        Assert.Equal("open", fill.Position.Status);
        Assert.Equal(10.025m, fill.Position.CostUsd);
        Assert.Equal(executedAt, fill.Position.OpenedAt);
        Assert.Equal(fill.Position.Id, fill.Trade.PositionId);
        Assert.Equal(order.Id, fill.Trade.OrderId);

        var again = await _client.PostAsJsonAsync($"/orders/{order.Id}/fill",
            new FillOrderRequest(50000m, 0.0002m, 0.025m, null, 50750m, 49000m, null));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Single((await _client.GetFromJsonAsync<List<Position>>("/positions?status=open"))!);
    }

    [Fact]
    public async Task FillingEntryOrder_WithoutExits_Returns400_AndLeavesOrderOpen()
    {
        var order = await CreateOrder("buy", "entry");
        var res = await _client.PostAsJsonAsync($"/orders/{order.Id}/fill",
            new FillOrderRequest(50000m, 0.0002m, 0.025m, null, null, null, null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Single((await _client.GetFromJsonAsync<List<Order>>("/orders?status=open"))!);
    }

    [Fact]
    public async Task FillingSellOrder_ClosesPosition_WithPurposeAsReason()
    {
        var entry = await CreateOrder("buy", "entry");
        var opened = (await (await _client.PostAsJsonAsync($"/orders/{entry.Id}/fill",
            new FillOrderRequest(50000m, 0.0002m, 0.025m, null, 50750m, 49000m, null)))
            .Content.ReadFromJsonAsync<FillOrderResponse>())!;

        var exit = await CreateOrder("sell", "take_profit", opened.Position.Id, 50750m);
        var res = await _client.PostAsJsonAsync($"/orders/{exit.Id}/fill",
            new FillOrderRequest(50750m, 0.0002m, 0.02538m, null, null, null, null));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var fill = (await res.Content.ReadFromJsonAsync<FillOrderResponse>())!;

        Assert.Equal("closed", fill.Position.Status);
        Assert.Equal("take_profit", fill.Position.CloseReason);
        Assert.Equal(0.09962m, fill.Position.RealizedPnlUsd!.Value, 5);
        Assert.Equal(2, (await _client.GetFromJsonAsync<List<Trade>>("/trades"))!.Count);
    }

    [Fact]
    public async Task CancelledOrder_CannotBeFilled_AndFilledOrderCannotBeCancelled()
    {
        var cancelled = await CreateOrder("buy", "entry");
        var cancel = await _client.PostAsJsonAsync($"/orders/{cancelled.Id}/cancel", new CancelOrderRequest("timeout"));
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal("timeout", (await cancel.Content.ReadFromJsonAsync<Order>())!.Note);
        var fill = await _client.PostAsJsonAsync($"/orders/{cancelled.Id}/fill",
            new FillOrderRequest(50000m, 0.0002m, 0.025m, null, 50750m, 49000m, null));
        Assert.Equal(HttpStatusCode.Conflict, fill.StatusCode);

        var filled = await CreateOrder("buy", "entry");
        await _client.PostAsJsonAsync($"/orders/{filled.Id}/fill",
            new FillOrderRequest(50000m, 0.0002m, 0.025m, null, 50750m, 49000m, null));
        var late = await _client.PostAsJsonAsync($"/orders/{filled.Id}/cancel", new CancelOrderRequest(null));
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
    }
}

public sealed class ApiKeyTests : IDisposable
{
    private readonly ApiFactory _factory = new() { ApiKey = "secret" };

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task RequestsWithoutKey_AreRejected_ExceptHealth()
    {
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/opportunities")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/opportunities")).StatusCode);
    }
}
