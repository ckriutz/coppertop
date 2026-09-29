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

    // Screener hard rules.
    public decimal MaxSpreadPct { get; set; } = 0.30m;
    public decimal MinVolume24hUsd { get; set; } = 1_000_000m;
    public decimal MaxDrop24hPct { get; set; } = 8m;
    public bool RejectDowntrend { get; set; } = true;
    public int MinSimTrades { get; set; } = 3;
    public decimal MinSimAvgNetPct { get; set; } = 0m;
    public int MaxOpportunities { get; set; } = 5;

    // Replay of the Trader's dip strategy. Keep in step with the Trader's own settings.
    public int CandleIntervalMinutes { get; set; } = 5;
    public int SmaPeriod { get; set; } = 20;
    public decimal BandStdDevs { get; set; } = 1.5m;
    public decimal MakerFeePct { get; set; } = 0.25m;
    public decimal TakerFeePct { get; set; } = 0.40m;
    public decimal StopLossSlippagePct { get; set; } = 0.10m;

    // Pause between Kraken public calls (Kraken allows roughly one per second per IP, shared with the Trader).
    public int KrakenRequestDelayMs { get; set; } = 1100;
}

public sealed class CoppertopApiOptions
{
    public const string Section = "CoppertopApi";

    public string BaseUrl { get; set; } = "http://localhost:5057";
    public string ApiKey { get; set; } = "";
}
