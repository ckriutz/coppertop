using Coppertop.Research;
using Coppertop.Research.News;
using Microsoft.Extensions.Time.Testing;

namespace Coppertop.Research.Tests;

public class SonarParsingTests
{
    private static readonly OpenRouterOptions O = new();

    [Theory]
    [InlineData("""{"verdict":"block","reason":"Exploit drained $40M [1]."}""", true, "Exploit drained $40M .")]
    [InlineData("```json\n{\"verdict\": \"Allow\", \"reason\": \"Nothing specific.\"}\n```", false, "Nothing specific.")]
    [InlineData("Here you go: {\"verdict\":\"allow\"}", false, "no reason given")]
    public void ParseVerdict_ToleratesFencesAndCase(string content, bool block, string reason)
    {
        var v = SonarClient.ParseVerdict(content);
        Assert.Equal(block, v.Block);
        Assert.Equal(reason, v.Reason);
    }

    [Theory]
    [InlineData("no json at all")]
    [InlineData("""{"verdict":"maybe","reason":"x"}""")]
    public void ParseVerdict_RejectsUnclearReplies(string content) =>
        Assert.Throws<InvalidOperationException>(() => SonarClient.ParseVerdict(content));

    [Fact]
    public void ParseResponse_UsesReportedCost_AndCollectsSources()
    {
        const string json = """
        {"model":"perplexity/sonar","citations":["https://a.example","https://b.example"],
         "choices":[{"message":{"content":"{\"verdict\":\"allow\",\"reason\":\"Quiet.\"}",
           "annotations":[{"type":"url_citation","url_citation":{"url":"https://b.example"}}]}}],
         "usage":{"prompt_tokens":210,"completion_tokens":30,"cost":0.00524}}
        """;
        var r = SonarClient.ParseResponse(json, O);
        Assert.False(r.Verdict.Block);
        Assert.Equal(210, r.InputTokens);
        Assert.Equal(30, r.OutputTokens);
        Assert.Equal(0.00524m, r.CostUsd);
        Assert.Equal(["https://a.example", "https://b.example"], r.Sources);
    }

    [Fact]
    public void ParseResponse_EstimatesCost_WhenNotReported()
    {
        const string json = """{"choices":[{"message":{"content":"{\"verdict\":\"block\",\"reason\":\"Delisted.\"}"}}],"usage":{"prompt_tokens":1000000,"completion_tokens":0}}""";
        var r = SonarClient.ParseResponse(json, O);
        Assert.True(r.Verdict.Block);
        Assert.Equal(1m + 0.005m, r.CostUsd);
        Assert.Equal("perplexity/sonar", r.Model);
    }

    [Fact]
    public void ParseResponse_SurfacesOpenRouterErrors() =>
        Assert.Throws<InvalidOperationException>(() =>
            SonarClient.ParseResponse("""{"error":{"message":"No credits","code":402}}""", O));

    [Theory]
    [InlineData("XBTUSD", "Bitcoin (BTC)")]
    [InlineData("XDGUSD", "Dogecoin (DOGE)")]
    [InlineData("PEPEUSD", "the PEPE cryptocurrency")]
    public void CoinName_MapsKrakenCodes(string asset, string name) => Assert.Equal(name, SonarClient.CoinName(asset));
}

public class NewsGateTests
{
    private static readonly ResearchOptions O = new() { NewsCacheHours = 6, NewsDailyBudgetUsd = 0.02m };

    private static SonarResult Result(bool block) => new(new NewsVerdict(block, block ? "hack" : "quiet"), "perplexity/sonar", 200, 30, 0.0052m, []);

    [Fact]
    public async Task Disabled_DoesNotCall()
    {
        var gate = new NewsGate(new FakeTimeProvider());
        var calls = 0;
        var outcome = await gate.CheckAsync("ETHUSD", false, 0m, O, 0.006m, (_, _) => { calls++; return Task.FromResult(Result(false)); }, default);
        Assert.Equal(NewsStatus.Disabled, outcome.Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CachesVerdict_UntilItExpires()
    {
        var clock = new FakeTimeProvider();
        var gate = new NewsGate(clock);
        var calls = 0;
        Task<SonarResult> Check(string _, CancellationToken __) { calls++; return Task.FromResult(Result(true)); }

        var first = await gate.CheckAsync("ETHUSD", true, 0m, O, 0.006m, Check, default);
        Assert.Equal(NewsStatus.Blocked, first.Status);
        Assert.NotNull(first.Fresh);

        clock.Advance(TimeSpan.FromHours(5));
        var cached = await gate.CheckAsync("ETHUSD", true, 0m, O, 0.006m, Check, default);
        Assert.Equal(NewsStatus.Blocked, cached.Status);
        Assert.Null(cached.Fresh);   // no new spend to record
        Assert.Equal(1, calls);

        clock.Advance(TimeSpan.FromHours(2));
        await gate.CheckAsync("ETHUSD", true, 0m, O, 0.006m, Check, default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OverBudget_IsUnavailable_WithoutCalling()
    {
        var gate = new NewsGate(new FakeTimeProvider());
        var calls = 0;
        var outcome = await gate.CheckAsync("ETHUSD", true, 0.015m, O, 0.006m, (_, _) => { calls++; return Task.FromResult(Result(false)); }, default);
        Assert.Equal(NewsStatus.Unavailable, outcome.Status);
        Assert.Contains("budget", outcome.Reason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Failure_IsUnavailable_AndNotCached()
    {
        var gate = new NewsGate(new FakeTimeProvider());
        var calls = 0;
        Task<SonarResult> Boom(string _, CancellationToken __) { calls++; throw new HttpRequestException("OpenRouter 502"); }

        var outcome = await gate.CheckAsync("ETHUSD", true, 0m, O, 0.006m, Boom, default);
        Assert.Equal(NewsStatus.Unavailable, outcome.Status);
        Assert.Contains("502", outcome.Reason);

        await gate.CheckAsync("ETHUSD", true, 0m, O, 0.006m, Boom, default);
        Assert.Equal(2, calls);   // retried next cycle
    }
}
