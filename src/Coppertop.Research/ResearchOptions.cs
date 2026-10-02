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
    // Tuning: closed replay trades needed in the training part, and in the unseen test part.
    public int MinSimTrades { get; set; } = 3;
    public int MinTestTrades { get; set; } = 2;
    // The tuned setting must average more than this per trade (after fees) on the test part.
    public decimal MinSimAvgNetPct { get; set; } = 0m;
    public int MaxOpportunities { get; set; } = 5;

    // Per-coin tuning grid (empty = built-in defaults, see Tuner). 0 in TuneMaxHoldHours means no time stop.
    public List<decimal> TuneTakeProfitPcts { get; set; } = [];
    public List<decimal> TuneStopLossPcts { get; set; } = [];
    public List<decimal> TuneBandStdDevs { get; set; } = [];
    public List<int> TuneMaxHoldHours { get; set; } = [];
    public decimal TuneTrainFraction { get; set; } = 0.67m;
    // Skip TPs whose win wouldn't clear fees by this much (the Trader enforces the same floor).
    public decimal MinNetProfitPct { get; set; } = 0.30m;

    // Replay of the Trader's dip strategy. 15m candles: Kraken's 720-candle limit gives ~7.5 days to tune on.
    // TakeProfitPct/StopLossPct/BandStdDevs are the untuned defaults, shown when no setting passes training.
    public int CandleIntervalMinutes { get; set; } = 15;
    public int SmaPeriod { get; set; } = 20;
    public decimal BandStdDevs { get; set; } = 1.5m;
    public decimal MakerFeePct { get; set; } = 0.25m;
    public decimal TakerFeePct { get; set; } = 0.40m;
    public decimal StopLossSlippagePct { get; set; } = 0.10m;

    // Pause between Kraken public calls (Kraken allows roughly one per second per IP, shared with the Trader).
    public int KrakenRequestDelayMs { get; set; } = 1100;

    // News vetoes (Sonar via OpenRouter). Only coins that pass the screener are checked.
    public int NewsLookbackHours { get; set; } = 48;
    public int NewsCacheHours { get; set; } = 6;
    public int NewsVetoHours { get; set; } = 12;
    public decimal NewsDailyBudgetUsd { get; set; } = 0.25m;
    public bool PublishWhenNewsUnavailable { get; set; } = false;
}

public sealed class OpenRouterOptions
{
    public const string Section = "OpenRouter";

    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string ApiKey { get; set; } = "";
    public string NewsModel { get; set; } = "perplexity/sonar";

    // Used only when OpenRouter doesn't report the cost itself, and for the budget check before a call.
    public decimal InputUsdPerMillion { get; set; } = 1m;
    public decimal OutputUsdPerMillion { get; set; } = 1m;
    public decimal SearchUsdPerRequest { get; set; } = 0.005m;
}

public sealed class CoppertopApiOptions
{
    public const string Section = "CoppertopApi";

    public string BaseUrl { get; set; } = "http://localhost:5057";
    public string ApiKey { get; set; } = "";
}
