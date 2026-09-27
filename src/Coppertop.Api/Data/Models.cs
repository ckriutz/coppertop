namespace Coppertop.Api.Data;

public static class OpportunityStatus
{
    public const string Active = "active";
    public const string Consumed = "consumed";
    public const string Cancelled = "cancelled";
    public const string Superseded = "superseded";
    public const string Expired = "expired";
}

public static class PositionStatus
{
    public const string Open = "open";
    public const string Closed = "closed";
}

public static class OrderStatuses
{
    public const string Validated = "validated";
    public const string Open = "open";
    public const string Filled = "filled";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Validated, Open, Filled, Cancelled, "rejected", "simulated"];
    public static readonly string[] Pending = [Validated, Open];
}

public sealed class Opportunity
{
    public long Id { get; set; }
    public string Asset { get; set; } = "";
    public string Strategy { get; set; } = "";
    public decimal MaxEntryPrice { get; set; }
    public decimal TakeProfitPct { get; set; }
    public decimal StopLossPct { get; set; }
    public decimal MaxSpendUsd { get; set; }
    public decimal Confidence { get; set; }
    public string Reason { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed record CreateOpportunityRequest(
    string Asset,
    string Strategy,
    decimal MaxEntryPrice,
    decimal TakeProfitPct,
    decimal StopLossPct,
    decimal MaxSpendUsd,
    decimal Confidence,
    string Reason,
    DateTimeOffset ExpiresAt);

public sealed class Veto
{
    public long Id { get; set; }
    public string Asset { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed record CreateVetoRequest(string Asset, string Reason, DateTimeOffset ExpiresAt);

public sealed class Position
{
    public long Id { get; set; }
    public long? OpportunityId { get; set; }
    public string Asset { get; set; } = "";
    public decimal Volume { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal EntryFeeUsd { get; set; }
    public decimal CostUsd { get; set; }
    public decimal TakeProfitPrice { get; set; }
    public decimal StopLossPrice { get; set; }
    public string Status { get; set; } = "";
    public bool IsSimulated { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal? ExitPrice { get; set; }
    public decimal? ExitFeeUsd { get; set; }
    public decimal? RealizedPnlUsd { get; set; }
    public string? CloseReason { get; set; }
}

public sealed record OpenPositionRequest(
    long? OpportunityId,
    string Asset,
    decimal Volume,
    decimal EntryPrice,
    decimal EntryFeeUsd,
    decimal TakeProfitPrice,
    decimal StopLossPrice,
    bool IsSimulated);

public sealed record ClosePositionRequest(decimal ExitPrice, decimal ExitFeeUsd, string Reason);

public sealed class Order
{
    public long Id { get; set; }
    public long? OpportunityId { get; set; }
    public long? PositionId { get; set; }
    public string Asset { get; set; } = "";
    public string Side { get; set; } = "";
    public string OrderType { get; set; } = "";
    public string Purpose { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Volume { get; set; }
    public string Status { get; set; } = "";
    public string? KrakenTxId { get; set; }
    public bool IsSimulated { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed record CreateOrderRequest(
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

public sealed record UpdateOrderRequest(string Status, string? KrakenTxId, string? Note);

/// <summary>
/// Marks an open order filled in one transaction. A buy "entry" opens a position (TP/SL required);
/// a sell with a position id closes that position with the order's purpose as the reason.
/// </summary>
public sealed record FillOrderRequest(
    decimal Price,
    decimal Volume,
    decimal FeeUsd,
    DateTimeOffset? ExecutedAt,
    decimal? TakeProfitPrice,
    decimal? StopLossPrice,
    string? Note);

public sealed record CancelOrderRequest(string? Note);

public sealed record FillOrderResponse(Order Order, Position Position, Trade Trade);

public sealed class Trade
{
    public long Id { get; set; }
    public long? OrderId { get; set; }
    public long? PositionId { get; set; }
    public string Asset { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Volume { get; set; }
    public decimal FeeUsd { get; set; }
    public bool IsSimulated { get; set; }
    public DateTimeOffset ExecutedAt { get; set; }
}

public sealed record CreateTradeRequest(
    long? OrderId,
    long? PositionId,
    string Asset,
    string Side,
    decimal Price,
    decimal Volume,
    decimal FeeUsd,
    bool IsSimulated,
    DateTimeOffset? ExecutedAt);

public sealed class TokenUsage
{
    public long Id { get; set; }
    public string Service { get; set; } = "";
    public string Model { get; set; } = "";
    public string Purpose { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record CreateTokenUsageRequest(
    string Service,
    string Model,
    string Purpose,
    long InputTokens,
    long OutputTokens,
    decimal CostUsd);

public sealed record Summary(
    DateTimeOffset Since,
    int ClosedPositions,
    int WinningPositions,
    decimal RealizedPnlUsd,
    decimal FeesUsd,
    decimal TokenCostUsd,
    decimal NetAfterTokensUsd,
    int OpenPositions,
    decimal OpenExposureUsd,
    int ActiveOpportunities);
