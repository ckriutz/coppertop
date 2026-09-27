namespace Coppertop.Research;

public sealed class ResearchOptions
{
    public const string Section = "Research";

    public int CycleMinutes { get; set; } = 60;
    public int RetryMinutes { get; set; } = 1;
    public int OpportunityLifetimeHours { get; set; } = 4;
    public decimal TakeProfitPct { get; set; } = 1.5m;
    public decimal StopLossPct { get; set; } = 2.0m;
    public decimal MaxSpendUsd { get; set; } = 12m;
    public List<string> Watchlist { get; set; } = [];
}

public sealed class CoppertopApiOptions
{
    public const string Section = "CoppertopApi";

    public string BaseUrl { get; set; } = "http://localhost:5057";
    public string ApiKey { get; set; } = "";
}
