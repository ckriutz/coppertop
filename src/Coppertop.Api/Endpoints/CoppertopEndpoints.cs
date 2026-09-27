using Coppertop.Api.Data;
using Dapper;

namespace Coppertop.Api.Endpoints;

public static class CoppertopEndpoints
{
    public static void MapCoppertopEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        MapOpportunities(app);
        MapVetoes(app);
        MapPositions(app);
        MapOrders(app);
        MapTrades(app);
        MapTokenUsage(app);
        MapSummary(app);
    }

    private static void MapOpportunities(WebApplication app)
    {
        var group = app.MapGroup("/opportunities");

        group.MapGet("/", (Database db, TimeProvider clock, bool? active) =>
        {
            using var c = db.Open();
            ExpireOpportunities(c, clock.GetUtcNow());
            var sql = active == true
                ? "SELECT * FROM opportunities WHERE status = @status ORDER BY confidence DESC, id DESC"
                : "SELECT * FROM opportunities ORDER BY id DESC LIMIT 500";
            return Results.Ok(c.Query<Opportunity>(sql, new { status = OpportunityStatus.Active }));
        });

        group.MapGet("/{id:long}", (Database db, long id) =>
        {
            using var c = db.Open();
            var item = c.QuerySingleOrDefault<Opportunity>("SELECT * FROM opportunities WHERE id = @id", new { id });
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPost("/", (Database db, TimeProvider clock, CreateOpportunityRequest req) =>
        {
            var now = clock.GetUtcNow();
            var errors = Validation.Opportunity(req, now);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var asset = Normalize(req.Asset);
            var strategy = req.Strategy.Trim().ToLowerInvariant();

            using var c = db.Open();
            using var tx = c.BeginTransaction();

            var vetoed = c.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM vetoes WHERE asset = @asset AND expires_at > @now",
                new { asset, now = Database.Ts(now) }, tx) > 0;
            if (vetoed)
                return Results.Conflict(new { error = $"Asset {asset} is currently vetoed." });

            // A newer opportunity for the same asset+strategy replaces the previous one.
            c.Execute("""
                UPDATE opportunities SET status = @superseded, closed_at = @now
                WHERE asset = @asset AND strategy = @strategy AND status = @active
                """,
                new { superseded = OpportunityStatus.Superseded, active = OpportunityStatus.Active, asset, strategy, now = Database.Ts(now) }, tx);

            var id = c.ExecuteScalar<long>("""
                INSERT INTO opportunities (asset, strategy, max_entry_price, take_profit_pct, stop_loss_pct,
                    max_spend_usd, confidence, reason, status, created_at, expires_at)
                VALUES (@asset, @strategy, @MaxEntryPrice, @TakeProfitPct, @StopLossPct,
                    @MaxSpendUsd, @Confidence, @Reason, @status, @createdAt, @expiresAt);
                SELECT last_insert_rowid();
                """,
                new
                {
                    asset, strategy, req.MaxEntryPrice, req.TakeProfitPct, req.StopLossPct, req.MaxSpendUsd,
                    req.Confidence, req.Reason, status = OpportunityStatus.Active,
                    createdAt = Database.Ts(now), expiresAt = Database.Ts(req.ExpiresAt)
                }, tx);
            tx.Commit();

            var created = c.QuerySingle<Opportunity>("SELECT * FROM opportunities WHERE id = @id", new { id });
            return Results.Created($"/opportunities/{id}", created);
        });

        group.MapPost("/{id:long}/consume", (Database db, TimeProvider clock, long id) =>
            CloseOpportunity(db, clock, id, OpportunityStatus.Consumed));

        group.MapPost("/{id:long}/cancel", (Database db, TimeProvider clock, long id) =>
            CloseOpportunity(db, clock, id, OpportunityStatus.Cancelled));
    }

    private static IResult CloseOpportunity(Database db, TimeProvider clock, long id, string newStatus)
    {
        var now = clock.GetUtcNow();
        using var c = db.Open();
        ExpireOpportunities(c, now);
        // Conditional update makes consume atomic: only one caller can move it out of "active".
        var changed = c.Execute(
            "UPDATE opportunities SET status = @newStatus, closed_at = @now WHERE id = @id AND status = @active",
            new { newStatus, now = Database.Ts(now), id, active = OpportunityStatus.Active });
        if (changed == 0)
        {
            var exists = c.ExecuteScalar<long>("SELECT COUNT(*) FROM opportunities WHERE id = @id", new { id }) > 0;
            return exists ? Results.Conflict(new { error = "Opportunity is not active." }) : Results.NotFound();
        }
        return Results.Ok(c.QuerySingle<Opportunity>("SELECT * FROM opportunities WHERE id = @id", new { id }));
    }

    private static void ExpireOpportunities(System.Data.IDbConnection c, DateTimeOffset now) =>
        c.Execute(
            "UPDATE opportunities SET status = @expired, closed_at = @now WHERE status = @active AND expires_at <= @now",
            new { expired = OpportunityStatus.Expired, active = OpportunityStatus.Active, now = Database.Ts(now) });

    private static void MapVetoes(WebApplication app)
    {
        var group = app.MapGroup("/vetoes");

        group.MapGet("/", (Database db, TimeProvider clock, bool? active) =>
        {
            using var c = db.Open();
            var sql = active == true
                ? "SELECT * FROM vetoes WHERE expires_at > @now ORDER BY id DESC"
                : "SELECT * FROM vetoes ORDER BY id DESC LIMIT 500";
            return Results.Ok(c.Query<Veto>(sql, new { now = Database.Ts(clock.GetUtcNow()) }));
        });

        group.MapPost("/", (Database db, TimeProvider clock, CreateVetoRequest req) =>
        {
            var now = clock.GetUtcNow();
            var errors = Validation.Veto(req, now);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var asset = Normalize(req.Asset);
            using var c = db.Open();
            using var tx = c.BeginTransaction();
            var id = c.ExecuteScalar<long>("""
                INSERT INTO vetoes (asset, reason, created_at, expires_at) VALUES (@asset, @Reason, @now, @expiresAt);
                SELECT last_insert_rowid();
                """, new { asset, req.Reason, now = Database.Ts(now), expiresAt = Database.Ts(req.ExpiresAt) }, tx);
            c.Execute(
                "UPDATE opportunities SET status = @cancelled, closed_at = @now WHERE asset = @asset AND status = @active",
                new { cancelled = OpportunityStatus.Cancelled, active = OpportunityStatus.Active, asset, now = Database.Ts(now) }, tx);
            tx.Commit();

            return Results.Created($"/vetoes/{id}", c.QuerySingle<Veto>("SELECT * FROM vetoes WHERE id = @id", new { id }));
        });
    }

    private static void MapPositions(WebApplication app)
    {
        var group = app.MapGroup("/positions");

        group.MapGet("/", (Database db, string? status) =>
        {
            using var c = db.Open();
            var sql = status is null
                ? "SELECT * FROM positions ORDER BY id DESC LIMIT 500"
                : "SELECT * FROM positions WHERE status = @status ORDER BY id DESC";
            return Results.Ok(c.Query<Position>(sql, new { status }));
        });

        group.MapGet("/{id:long}", (Database db, long id) =>
        {
            using var c = db.Open();
            var item = c.QuerySingleOrDefault<Position>("SELECT * FROM positions WHERE id = @id", new { id });
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPost("/", (Database db, TimeProvider clock, OpenPositionRequest req) =>
        {
            var errors = Validation.Position(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var now = clock.GetUtcNow();
            var cost = req.EntryPrice * req.Volume + req.EntryFeeUsd;
            using var c = db.Open();
            var id = c.ExecuteScalar<long>("""
                INSERT INTO positions (opportunity_id, asset, volume, entry_price, entry_fee_usd, cost_usd,
                    take_profit_price, stop_loss_price, status, is_simulated, opened_at)
                VALUES (@OpportunityId, @asset, @Volume, @EntryPrice, @EntryFeeUsd, @cost,
                    @TakeProfitPrice, @StopLossPrice, @status, @IsSimulated, @now);
                SELECT last_insert_rowid();
                """,
                new
                {
                    req.OpportunityId, asset = Normalize(req.Asset), req.Volume, req.EntryPrice, req.EntryFeeUsd, cost,
                    req.TakeProfitPrice, req.StopLossPrice, status = PositionStatus.Open, req.IsSimulated,
                    now = Database.Ts(now)
                });
            return Results.Created($"/positions/{id}", c.QuerySingle<Position>("SELECT * FROM positions WHERE id = @id", new { id }));
        });

        group.MapPost("/{id:long}/close", (Database db, TimeProvider clock, long id, ClosePositionRequest req) =>
        {
            var errors = Validation.ClosePosition(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            using var c = db.Open();
            var position = c.QuerySingleOrDefault<Position>("SELECT * FROM positions WHERE id = @id", new { id });
            if (position is null) return Results.NotFound();
            if (position.Status != PositionStatus.Open) return Results.Conflict(new { error = "Position is not open." });

            var pnl = req.ExitPrice * position.Volume - req.ExitFeeUsd - position.CostUsd;
            c.Execute("""
                UPDATE positions SET status = @closed, closed_at = @now, exit_price = @ExitPrice,
                    exit_fee_usd = @ExitFeeUsd, realized_pnl_usd = @pnl, close_reason = @Reason
                WHERE id = @id AND status = @open
                """,
                new
                {
                    closed = PositionStatus.Closed, open = PositionStatus.Open, now = Database.Ts(clock.GetUtcNow()),
                    req.ExitPrice, req.ExitFeeUsd, pnl, req.Reason, id
                });
            return Results.Ok(c.QuerySingle<Position>("SELECT * FROM positions WHERE id = @id", new { id }));
        });
    }

    private static void MapOrders(WebApplication app)
    {
        var group = app.MapGroup("/orders");

        group.MapGet("/", (Database db, string? status) =>
        {
            using var c = db.Open();
            var sql = status is null
                ? "SELECT * FROM orders ORDER BY id DESC LIMIT 500"
                : "SELECT * FROM orders WHERE status = @status ORDER BY id DESC";
            return Results.Ok(c.Query<Order>(sql, new { status }));
        });

        group.MapPost("/", (Database db, TimeProvider clock, CreateOrderRequest req) =>
        {
            var errors = Validation.Order(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var now = Database.Ts(clock.GetUtcNow());
            using var c = db.Open();
            var id = c.ExecuteScalar<long>("""
                INSERT INTO orders (opportunity_id, position_id, asset, side, order_type, purpose, price, volume,
                    status, kraken_tx_id, is_simulated, note, created_at, updated_at)
                VALUES (@OpportunityId, @PositionId, @asset, @side, @OrderType, @Purpose, @Price, @Volume,
                    @Status, @KrakenTxId, @IsSimulated, @Note, @now, @now);
                SELECT last_insert_rowid();
                """,
                new
                {
                    req.OpportunityId, req.PositionId, asset = Normalize(req.Asset), side = req.Side.ToLowerInvariant(),
                    req.OrderType, req.Purpose, req.Price, req.Volume, req.Status, req.KrakenTxId, req.IsSimulated,
                    req.Note, now
                });
            return Results.Created($"/orders/{id}", c.QuerySingle<Order>("SELECT * FROM orders WHERE id = @id", new { id }));
        });

        group.MapPatch("/{id:long}", (Database db, TimeProvider clock, long id, UpdateOrderRequest req) =>
        {
            if (!OrderStatuses.All.Contains(req.Status))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["status"] = [$"Must be one of: {string.Join(", ", OrderStatuses.All)}"]
                });

            using var c = db.Open();
            var changed = c.Execute("""
                UPDATE orders SET status = @Status, kraken_tx_id = COALESCE(@KrakenTxId, kraken_tx_id),
                    note = COALESCE(@Note, note), updated_at = @now
                WHERE id = @id
                """, new { req.Status, req.KrakenTxId, req.Note, now = Database.Ts(clock.GetUtcNow()), id });
            return changed == 0
                ? Results.NotFound()
                : Results.Ok(c.QuerySingle<Order>("SELECT * FROM orders WHERE id = @id", new { id }));
        });
    }

    private static void MapTrades(WebApplication app)
    {
        var group = app.MapGroup("/trades");

        group.MapGet("/", (Database db) =>
        {
            using var c = db.Open();
            return Results.Ok(c.Query<Trade>("SELECT * FROM trades ORDER BY executed_at DESC LIMIT 500"));
        });

        group.MapPost("/", (Database db, TimeProvider clock, CreateTradeRequest req) =>
        {
            var errors = Validation.Trade(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            using var c = db.Open();
            var id = c.ExecuteScalar<long>("""
                INSERT INTO trades (order_id, position_id, asset, side, price, volume, fee_usd, is_simulated, executed_at)
                VALUES (@OrderId, @PositionId, @asset, @side, @Price, @Volume, @FeeUsd, @IsSimulated, @executedAt);
                SELECT last_insert_rowid();
                """,
                new
                {
                    req.OrderId, req.PositionId, asset = Normalize(req.Asset), side = req.Side.ToLowerInvariant(),
                    req.Price, req.Volume, req.FeeUsd, req.IsSimulated,
                    executedAt = Database.Ts(req.ExecutedAt ?? clock.GetUtcNow())
                });
            return Results.Created($"/trades/{id}", c.QuerySingle<Trade>("SELECT * FROM trades WHERE id = @id", new { id }));
        });
    }

    private static void MapTokenUsage(WebApplication app)
    {
        var group = app.MapGroup("/token-usage");

        group.MapGet("/", (Database db) =>
        {
            using var c = db.Open();
            return Results.Ok(c.Query<TokenUsage>("SELECT * FROM token_usage ORDER BY id DESC LIMIT 500"));
        });

        group.MapPost("/", (Database db, TimeProvider clock, CreateTokenUsageRequest req) =>
        {
            var errors = Validation.TokenUsage(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            using var c = db.Open();
            var id = c.ExecuteScalar<long>("""
                INSERT INTO token_usage (service, model, purpose, input_tokens, output_tokens, cost_usd, created_at)
                VALUES (@Service, @Model, @Purpose, @InputTokens, @OutputTokens, @CostUsd, @now);
                SELECT last_insert_rowid();
                """,
                new { req.Service, req.Model, req.Purpose, req.InputTokens, req.OutputTokens, req.CostUsd, now = Database.Ts(clock.GetUtcNow()) });
            return Results.Created($"/token-usage/{id}", c.QuerySingle<TokenUsage>("SELECT * FROM token_usage WHERE id = @id", new { id }));
        });
    }

    private static void MapSummary(WebApplication app)
    {
        app.MapGet("/summary", (Database db, TimeProvider clock, DateTimeOffset? since) =>
        {
            var from = since ?? DateTimeOffset.UnixEpoch;
            var p = new { since = Database.Ts(from), open = PositionStatus.Open, closed = PositionStatus.Closed, now = Database.Ts(clock.GetUtcNow()), active = OpportunityStatus.Active };
            using var c = db.Open();

            var closed = c.QuerySingle<(long Count, long Wins, double Pnl, double Fees)>("""
                SELECT COUNT(*), COALESCE(SUM(CASE WHEN realized_pnl_usd > 0 THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(realized_pnl_usd), 0), COALESCE(SUM(entry_fee_usd + COALESCE(exit_fee_usd, 0)), 0)
                FROM positions WHERE status = @closed AND closed_at >= @since
                """, p);
            var tokens = c.ExecuteScalar<double>("SELECT COALESCE(SUM(cost_usd), 0) FROM token_usage WHERE created_at >= @since", p);
            var open = c.QuerySingle<(long Count, double Exposure)>(
                "SELECT COUNT(*), COALESCE(SUM(cost_usd), 0) FROM positions WHERE status = @open", p);
            var activeOpps = c.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM opportunities WHERE status = @active AND expires_at > @now", p);

            var pnl = (decimal)closed.Pnl;
            var tokenCost = (decimal)tokens;
            return Results.Ok(new Summary(
                from, (int)closed.Count, (int)closed.Wins, pnl, (decimal)closed.Fees, tokenCost, pnl - tokenCost,
                (int)open.Count, (decimal)open.Exposure, (int)activeOpps));
        });
    }

    private static string Normalize(string asset) => asset.Trim().ToUpperInvariant();
}
