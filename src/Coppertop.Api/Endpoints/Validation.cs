using Coppertop.Api.Data;

namespace Coppertop.Api.Endpoints;

public static class Validation
{
    private static readonly string[] Sides = ["buy", "sell"];
    private static readonly string[] OrderPurposes = ["entry", "take_profit", "stop_loss", "time_stop", "flatten", "manual"];

    public static Dictionary<string, string[]> Opportunity(CreateOpportunityRequest r, DateTimeOffset now)
    {
        var e = new Errors();
        e.Require(!string.IsNullOrWhiteSpace(r.Asset), "asset", "Required.");
        e.Require(!string.IsNullOrWhiteSpace(r.Strategy), "strategy", "Required.");
        e.Require(r.MaxEntryPrice > 0, "maxEntryPrice", "Must be > 0.");
        e.Require(r.TakeProfitPct is > 0 and <= 50, "takeProfitPct", "Must be in (0, 50].");
        e.Require(r.StopLossPct is > 0 and <= 50, "stopLossPct", "Must be in (0, 50].");
        e.Require(r.MaxSpendUsd > 0, "maxSpendUsd", "Must be > 0.");
        e.Require(r.Confidence is >= 0 and <= 1, "confidence", "Must be between 0 and 1.");
        e.Require(!string.IsNullOrWhiteSpace(r.Reason), "reason", "Required.");
        e.Require(r.ExpiresAt > now, "expiresAt", "Must be in the future.");
        if (r.Signal is { } s)
        {
            e.Require(s.CandleIntervalMinutes is 1 or 5 or 15 or 30 or 60 or 240 or 1440, "signal.candleIntervalMinutes", "Must be a Kraken OHLC interval (1, 5, 15, 30, 60, 240, 1440).");
            e.Require(s.SmaPeriod is >= 2 and <= 500, "signal.smaPeriod", "Must be in [2, 500].");
            e.Require(s.BandStdDevs is >= 0 and <= 10, "signal.bandStdDevs", "Must be in [0, 10].");
            e.Require(s.MaxHoldMinutes is null or > 0, "signal.maxHoldMinutes", "Must be > 0 or null.");
        }
        return e.Result;
    }

    public static Dictionary<string, string[]> Veto(CreateVetoRequest r, DateTimeOffset now)
    {
        var e = new Errors();
        e.Require(!string.IsNullOrWhiteSpace(r.Asset), "asset", "Required.");
        e.Require(!string.IsNullOrWhiteSpace(r.Reason), "reason", "Required.");
        e.Require(r.ExpiresAt > now, "expiresAt", "Must be in the future.");
        return e.Result;
    }

    public static Dictionary<string, string[]> Position(OpenPositionRequest r)
    {
        var e = new Errors();
        e.Require(!string.IsNullOrWhiteSpace(r.Asset), "asset", "Required.");
        e.Require(r.Volume > 0, "volume", "Must be > 0.");
        e.Require(r.EntryPrice > 0, "entryPrice", "Must be > 0.");
        e.Require(r.EntryFeeUsd >= 0, "entryFeeUsd", "Must be >= 0.");
        e.Require(r.TakeProfitPrice > r.EntryPrice, "takeProfitPrice", "Must be above entryPrice.");
        e.Require(r.StopLossPrice > 0 && r.StopLossPrice < r.EntryPrice, "stopLossPrice", "Must be between 0 and entryPrice.");
        return e.Result;
    }

    public static Dictionary<string, string[]> ClosePosition(ClosePositionRequest r)
    {
        var e = new Errors();
        e.Require(r.ExitPrice > 0, "exitPrice", "Must be > 0.");
        e.Require(r.ExitFeeUsd >= 0, "exitFeeUsd", "Must be >= 0.");
        e.Require(!string.IsNullOrWhiteSpace(r.Reason), "reason", "Required.");
        return e.Result;
    }

    public static Dictionary<string, string[]> Order(CreateOrderRequest r)
    {
        var e = new Errors();
        e.Require(!string.IsNullOrWhiteSpace(r.Asset), "asset", "Required.");
        e.Require(Sides.Contains(r.Side?.ToLowerInvariant()), "side", "Must be buy or sell.");
        e.Require(!string.IsNullOrWhiteSpace(r.OrderType), "orderType", "Required.");
        e.Require(OrderPurposes.Contains(r.Purpose), "purpose", $"Must be one of: {string.Join(", ", OrderPurposes)}.");
        e.Require(r.Price > 0, "price", "Must be > 0.");
        e.Require(r.Volume > 0, "volume", "Must be > 0.");
        e.Require(OrderStatuses.All.Contains(r.Status), "status", $"Must be one of: {string.Join(", ", OrderStatuses.All)}.");
        return e.Result;
    }

    public static Dictionary<string, string[]> Fill(FillOrderRequest r, Order order)
    {
        var e = new Errors();
        e.Require(r.Price > 0, "price", "Must be > 0.");
        e.Require(r.Volume > 0, "volume", "Must be > 0.");
        e.Require(r.FeeUsd >= 0, "feeUsd", "Must be >= 0.");
        if (order.Side == "buy")
        {
            e.Require(order.Purpose == "entry", "purpose", "Only buy orders with purpose 'entry' can be filled.");
            e.Require(r.TakeProfitPrice > r.Price, "takeProfitPrice", "Required and must be above price.");
            e.Require(r.StopLossPrice > 0 && r.StopLossPrice < r.Price, "stopLossPrice", "Required and must be between 0 and price.");
        }
        else
        {
            e.Require(order.PositionId is not null, "positionId", "Sell orders need a position to close.");
        }
        return e.Result;
    }

    public static Dictionary<string, string[]> Trade(CreateTradeRequest r)
    {
        var e = new Errors();
        e.Require(!string.IsNullOrWhiteSpace(r.Asset), "asset", "Required.");
        e.Require(Sides.Contains(r.Side?.ToLowerInvariant()), "side", "Must be buy or sell.");
        e.Require(r.Price > 0, "price", "Must be > 0.");
        e.Require(r.Volume > 0, "volume", "Must be > 0.");
        e.Require(r.FeeUsd >= 0, "feeUsd", "Must be >= 0.");
        return e.Result;
    }

    public static Dictionary<string, string[]> Heartbeat(HeartbeatRequest r)
    {
        var e = new Errors();
        e.Require(!string.IsNullOrWhiteSpace(r.Mode), "mode", "Required.");
        e.Require(r.CycleSeconds > 0, "cycleSeconds", "Must be > 0.");
        e.Require(r.PaperStartingCashUsd >= 0, "paperStartingCashUsd", "Must be >= 0.");
        e.Require((r.Marks ?? []).All(m => !string.IsNullOrWhiteSpace(m.Asset) && m.Bid > 0 && m.Ask > 0 && m.Last > 0),
            "marks", "Each mark needs an asset and positive bid, ask and last.");
        return e.Result;
    }

    public static Dictionary<string, string[]> AccountSnapshot(AccountSnapshotRequest r)
    {
        var e = new Errors();
        e.Require(r.Balances is not null || !string.IsNullOrWhiteSpace(r.Error), "balances", "Send balances or an error.");
        var b = r.Balances ?? [];
        e.Require(b.All(x => !string.IsNullOrWhiteSpace(x.Asset) && !string.IsNullOrWhiteSpace(x.DisplayName)),
            "balances", "Each balance needs an asset and display name.");
        e.Require(b.All(x => x.Hold >= 0 && x.PriceUsd is null or >= 0), "balances", "Hold and price must be >= 0.");
        e.Require(b.Select(x => (x.Asset ?? "").Trim().ToUpperInvariant()).Distinct().Count() == b.Count, "balances", "Duplicate asset.");
        return e.Result;
    }

    public static Dictionary<string, string[]> Screen(PublishScreenRequest r)
    {
        var e = new Errors();
        var results = r.Results ?? [];
        e.Require(results.All(x => !string.IsNullOrWhiteSpace(x.Asset) && !string.IsNullOrWhiteSpace(x.Reason)),
            "results", "Each result needs an asset and a reason.");
        e.Require(results.All(x => x.Confidence is >= 0 and <= 1), "results", "Confidence must be between 0 and 1.");
        e.Require(results.Select(x => (x.Asset ?? "").Trim().ToUpperInvariant()).Distinct().Count() == results.Count, "results", "Duplicate asset.");
        return e.Result;
    }

    public static Dictionary<string, string[]> TokenUsage(CreateTokenUsageRequest r)
    {
        var e = new Errors();
        e.Require(!string.IsNullOrWhiteSpace(r.Service), "service", "Required.");
        e.Require(!string.IsNullOrWhiteSpace(r.Model), "model", "Required.");
        e.Require(!string.IsNullOrWhiteSpace(r.Purpose), "purpose", "Required.");
        e.Require(r.InputTokens >= 0, "inputTokens", "Must be >= 0.");
        e.Require(r.OutputTokens >= 0, "outputTokens", "Must be >= 0.");
        e.Require(r.CostUsd >= 0, "costUsd", "Must be >= 0.");
        return e.Result;
    }

    private sealed class Errors
    {
        public Dictionary<string, string[]> Result { get; } = new();

        public void Require(bool condition, string field, string message)
        {
            if (!condition) Result[field] = [message];
        }
    }
}
