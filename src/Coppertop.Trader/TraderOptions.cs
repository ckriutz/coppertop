namespace Coppertop.Trader;

public sealed class TraderOptions
{
    public const string Section = "Trader";

    // "Paper" only in this build. Live trading is intentionally not enabled yet.
    public string Mode { get; set; } = "Paper";
    public int CycleSeconds { get; set; } = 30;
    public decimal PaperStartingCashUsd { get; set; } = 100m;

    public decimal MinTradeUsd { get; set; } = 7m;
    public decimal MaxTradeUsd { get; set; } = 15m;
    public int MaxOpenPositions { get; set; } = 5;
    public decimal MaxPerAssetExposureUsd { get; set; } = 15m;

    public decimal MakerFeePct { get; set; } = 0.25m;
    public decimal TakerFeePct { get; set; } = 0.40m;
    public decimal MinNetProfitPct { get; set; } = 0.30m;
    public decimal MaxSpreadPct { get; set; } = 0.30m;

    public int CandleIntervalMinutes { get; set; } = 5;
    public int SmaPeriod { get; set; } = 20;
    public decimal BandStdDevs { get; set; } = 1.5m;

    // Paper fill model: resting limits only fill when the market trades through them.
    public int EntryOrderTimeoutMinutes { get; set; } = 15;
    public decimal EntryOrderRunawayPct { get; set; } = 0.5m;
    public decimal StopLossSlippagePct { get; set; } = 0.10m;
    public int TradesLookbackMinutes { get; set; } = 60;
    public int TradesMaxPages { get; set; } = 5;
}

public sealed class CoppertopApiOptions
{
    public const string Section = "CoppertopApi";

    public string BaseUrl { get; set; } = "http://localhost:5057";
    public string ApiKey { get; set; } = "";
}

public sealed class KrakenOptions
{
    public const string Section = "Kraken";

    public string BaseUrl { get; set; } = "https://api.kraken.com";
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";

    // How often to read balances from the Kraken account (private API) when credentials are set.
    public int AccountRefreshSeconds { get; set; } = 60;

    public bool HasCredentials => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}
