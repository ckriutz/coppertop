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
        MapStats(app);
        MapControl(app);
        MapAccount(app);
        MapResearch(app);
    }

    private static void MapResearch(WebApplication app)
    {
        var group = app.MapGroup("/research");

        group.MapGet("/screen", (Database db) =>
        {
            using var c = db.Open();
            var rows = c.Query<ScreenRow>("SELECT * FROM research_screen ORDER BY approved DESC, confidence DESC, asset");
            return Results.Ok(rows.Select(r => new ScreenResult(
                r.Asset, r.Approved, r.Confidence, r.Reason,
                r.Metrics is null ? null : System.Text.Json.JsonDocument.Parse(r.Metrics).RootElement.Clone(),
                r.ScreenedAt)));
        });

        // Research posts every watchlist verdict each cycle; the previous set is replaced.
        group.MapPost("/screen", (Database db, TimeProvider clock, PublishScreenRequest req) =>
        {
            var errors = Validation.Screen(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var now = Database.Ts(clock.GetUtcNow());
            using var c = db.Open();
            using var tx = c.BeginTransaction();
            c.Execute("DELETE FROM research_screen", transaction: tx);
            foreach (var r in req.Results)
            {
                c.Execute("""
                    INSERT INTO research_screen (asset, approved, confidence, reason, metrics, screened_at)
                    VALUES (@asset, @Approved, @Confidence, @Reason, @metrics, @now)
                    """,
                    new
                    {
                        asset = Normalize(r.Asset), r.Approved, r.Confidence, r.Reason,
                        metrics = r.Metrics is { ValueKind: not System.Text.Json.JsonValueKind.Null } m ? m.GetRawText() : null,
                        now
                    }, tx);
            }
            tx.Commit();
            return Results.NoContent();
        });
    }

    private static void MapAccount(WebApplication app)
    {
        var group = app.MapGroup("/account");

        group.MapGet("/", (Database db) =>
        {
            using var c = db.Open();
            var status = c.QuerySingle<AccountStatus>("SELECT synced_at, error, error_at FROM account_status WHERE id = 1");
            var balances = c.Query<AccountBalance>("SELECT * FROM account_balances")
                .OrderByDescending(b => b.ValueUsd ?? -1).ThenBy(b => b.DisplayName).ToList();
            var cash = balances.Where(IsUsd).ToList();
            return Results.Ok(new Account(
                status.SyncedAt is not null,
                status.SyncedAt,
                status.Error,
                status.ErrorAt,
                balances.Sum(b => b.ValueUsd ?? 0m),
                cash.Sum(b => b.Balance),
                cash.Sum(b => b.Available),
                balances.Count(b => b.PriceUsd is null),
                balances.Count(b => b.IsDust),
                balances.Where(b => b.IsDust).Sum(b => b.ValueUsd ?? 0m),
                balances));
        });

        // The Trader posts the latest Kraken balances (a full replacement) or the error it hit reading them.
        group.MapPost("/", (Database db, TimeProvider clock, AccountSnapshotRequest req) =>
        {
            var errors = Validation.AccountSnapshot(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var now = Database.Ts(clock.GetUtcNow());
            using var c = db.Open();
            using var tx = c.BeginTransaction();
            if (req.Balances is { } balances)
            {
                c.Execute("DELETE FROM account_balances", transaction: tx);
                foreach (var b in balances)
                {
                    c.Execute("""
                        INSERT INTO account_balances (asset, display_name, balance, hold, price_usd, is_dust, updated_at)
                        VALUES (@asset, @DisplayName, @Balance, @Hold, @PriceUsd, @IsDust, @now)
                        """, new { asset = Normalize(b.Asset), b.DisplayName, b.Balance, b.Hold, b.PriceUsd, b.IsDust, now }, tx);
                }
                c.Execute("UPDATE account_status SET synced_at = @now, error = NULL, error_at = NULL WHERE id = 1", new { now }, tx);
            }
            else
            {
                c.Execute("UPDATE account_status SET error = @Error, error_at = @now WHERE id = 1", new { req.Error, now }, tx);
            }
            tx.Commit();
            return Results.NoContent();
        });
    }

    private static bool IsUsd(AccountBalance b) => b.Asset.Split('.')[0] is "ZUSD" or "USD";

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

        group.MapPost("/{id:long}/fill", (Database db, TimeProvider clock, long id, FillOrderRequest req) => FillOrder(db, clock, id, req));

        group.MapPost("/{id:long}/cancel", (Database db, TimeProvider clock, long id, CancelOrderRequest? req) =>
        {
            using var c = db.Open();
            var changed = c.Execute("""
                UPDATE orders SET status = @cancelled, note = COALESCE(@note, note), updated_at = @now
                WHERE id = @id AND status IN @pending
                """,
                new { cancelled = OrderStatuses.Cancelled, note = req?.Note, now = Database.Ts(clock.GetUtcNow()), id, pending = OrderStatuses.Pending });
            if (changed == 0)
            {
                var exists = c.ExecuteScalar<long>("SELECT COUNT(*) FROM orders WHERE id = @id", new { id }) > 0;
                return exists ? Results.Conflict(new { error = "Order is not open." }) : Results.NotFound();
            }
            return Results.Ok(c.QuerySingle<Order>("SELECT * FROM orders WHERE id = @id", new { id }));
        });
    }

    private static IResult FillOrder(Database db, TimeProvider clock, long id, FillOrderRequest req)
    {
        var now = clock.GetUtcNow();
        var executedAt = Database.Ts(req.ExecutedAt ?? now);
        using var c = db.Open();
        using var tx = c.BeginTransaction();

        var order = c.QuerySingleOrDefault<Order>("SELECT * FROM orders WHERE id = @id", new { id }, tx);
        if (order is null) return Results.NotFound();

        var errors = Validation.Fill(req, order);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        // Claim the order first: the conditional update makes fill/cancel races resolve to exactly one winner.
        var claimed = c.Execute("""
            UPDATE orders SET status = @filled, note = COALESCE(@Note, note), updated_at = @now
            WHERE id = @id AND status IN @pending
            """, new { filled = OrderStatuses.Filled, req.Note, now = Database.Ts(now), id, pending = OrderStatuses.Pending }, tx);
        if (claimed == 0) return Results.Conflict(new { error = "Order is not open." });

        long positionId;
        if (order.Side == "buy")
        {
            positionId = c.ExecuteScalar<long>("""
                INSERT INTO positions (opportunity_id, asset, volume, entry_price, entry_fee_usd, cost_usd,
                    take_profit_price, stop_loss_price, status, is_simulated, opened_at)
                VALUES (@OpportunityId, @Asset, @Volume, @Price, @FeeUsd, @cost,
                    @TakeProfitPrice, @StopLossPrice, @open, @IsSimulated, @executedAt);
                SELECT last_insert_rowid();
                """,
                new
                {
                    order.OpportunityId, order.Asset, req.Volume, req.Price, req.FeeUsd, cost = req.Price * req.Volume + req.FeeUsd,
                    req.TakeProfitPrice, req.StopLossPrice, open = PositionStatus.Open, order.IsSimulated, executedAt
                }, tx);
        }
        else
        {
            positionId = order.PositionId!.Value;
            var position = c.QuerySingleOrDefault<Position>("SELECT * FROM positions WHERE id = @positionId", new { positionId }, tx);
            if (position is null || position.Status != PositionStatus.Open)
                return Results.Conflict(new { error = "Position is not open." });

            var pnl = req.Price * position.Volume - req.FeeUsd - position.CostUsd;
            c.Execute("""
                UPDATE positions SET status = @closed, closed_at = @executedAt, exit_price = @Price,
                    exit_fee_usd = @FeeUsd, realized_pnl_usd = @pnl, close_reason = @reason
                WHERE id = @positionId
                """,
                new { closed = PositionStatus.Closed, executedAt, req.Price, req.FeeUsd, pnl, reason = order.Purpose, positionId }, tx);
        }

        var tradeId = c.ExecuteScalar<long>("""
            INSERT INTO trades (order_id, position_id, asset, side, price, volume, fee_usd, is_simulated, executed_at)
            VALUES (@id, @positionId, @Asset, @Side, @Price, @Volume, @FeeUsd, @IsSimulated, @executedAt);
            SELECT last_insert_rowid();
            """, new { id, positionId, order.Asset, order.Side, req.Price, req.Volume, req.FeeUsd, order.IsSimulated, executedAt }, tx);
        c.Execute("UPDATE orders SET position_id = @positionId WHERE id = @id", new { positionId, id }, tx);
        tx.Commit();

        return Results.Ok(new FillOrderResponse(
            c.QuerySingle<Order>("SELECT * FROM orders WHERE id = @id", new { id }),
            c.QuerySingle<Position>("SELECT * FROM positions WHERE id = @positionId", new { positionId }),
            c.QuerySingle<Trade>("SELECT * FROM trades WHERE id = @tradeId", new { tradeId })));
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

    private static void MapStats(WebApplication app)
    {
        app.MapGet("/stats", (Database db, DateTimeOffset? since) =>
        {
            var from = since ?? DateTimeOffset.UnixEpoch;
            using var c = db.Open();
            var closed = c.Query<Position>(
                "SELECT * FROM positions WHERE status = @closed AND closed_at >= @since",
                new { closed = PositionStatus.Closed, since = Database.Ts(from) }).ToList();
            var entries = c.Query<(string Status, long Count)>("""
                SELECT status, COUNT(*) FROM orders
                WHERE side = 'buy' AND purpose = 'entry' AND created_at >= @since GROUP BY status
                """, new { since = Database.Ts(from) }).ToDictionary(x => x.Status, x => (int)x.Count);

            var pnls = closed.Select(p => p.RealizedPnlUsd ?? 0m).ToList();
            var wins = pnls.Where(v => v > 0).ToList();
            var losses = pnls.Where(v => v <= 0).ToList();
            var grossProfit = wins.Sum();
            var grossLoss = -losses.Sum();

            var filled = entries.GetValueOrDefault(OrderStatuses.Filled);
            var cancelled = entries.GetValueOrDefault(OrderStatuses.Cancelled);
            var open = entries.GetValueOrDefault(OrderStatuses.Open);

            return Results.Ok(new Stats(
                from,
                closed.Count,
                wins.Count,
                losses.Count,
                closed.Count == 0 ? null : 100m * wins.Count / closed.Count,
                grossProfit,
                grossLoss,
                grossLoss == 0 ? null : grossProfit / grossLoss,
                pnls.Count == 0 ? null : pnls.Average(),
                wins.Count == 0 ? null : wins.Average(),
                losses.Count == 0 ? null : losses.Average(),
                pnls.Count == 0 ? null : pnls.Max(),
                pnls.Count == 0 ? null : pnls.Min(),
                closed.Count == 0 ? null : (decimal)closed.Average(p => (p.ClosedAt!.Value - p.OpenedAt).TotalMinutes),
                closed.GroupBy(p => p.CloseReason ?? "unknown")
                    .Select(g => new ReasonStats(g.Key, g.Count(), g.Sum(p => p.RealizedPnlUsd ?? 0m)))
                    .OrderByDescending(r => r.Count).ToList(),
                new EntryOrderStats(filled, cancelled, open,
                    filled + cancelled == 0 ? null : 100m * filled / (filled + cancelled))));
        });
    }

    private static void MapControl(WebApplication app)
    {
        const string select = "SELECT * FROM trader_control WHERE id = 1";

        app.MapGet("/marks", (Database db) =>
        {
            using var c = db.Open();
            return Results.Ok(c.Query<Mark>("SELECT * FROM marks ORDER BY asset"));
        });

        var group = app.MapGroup("/control");

        group.MapGet("/", (Database db) =>
        {
            using var c = db.Open();
            return Results.Ok(c.QuerySingle<TraderControl>(select));
        });

        // Pausing stops new entries (and the Trader cancels resting ones); exits keep running.
        group.MapPut("/", (Database db, TimeProvider clock, UpdateControlRequest req) =>
        {
            using var c = db.Open();
            c.Execute("UPDATE trader_control SET entries_paused = @EntriesPaused, updated_at = @now WHERE id = 1",
                new { req.EntriesPaused, now = Database.Ts(clock.GetUtcNow()) });
            return Results.Ok(c.QuerySingle<TraderControl>(select));
        });

        // Kill switch: pause entries and ask the Trader to sell everything at market on its next cycle.
        group.MapPost("/flatten", (Database db, TimeProvider clock) =>
        {
            using var c = db.Open();
            c.Execute("UPDATE trader_control SET entries_paused = 1, flatten_requested = 1, updated_at = @now WHERE id = 1",
                new { now = Database.Ts(clock.GetUtcNow()) });
            return Results.Ok(c.QuerySingle<TraderControl>(select));
        });

        group.MapPost("/flatten/ack", (Database db, TimeProvider clock) =>
        {
            using var c = db.Open();
            c.Execute("UPDATE trader_control SET flatten_requested = 0, updated_at = @now WHERE id = 1",
                new { now = Database.Ts(clock.GetUtcNow()) });
            return Results.Ok(c.QuerySingle<TraderControl>(select));
        });

        group.MapPost("/heartbeat", (Database db, TimeProvider clock, HeartbeatRequest req) =>
        {
            var errors = Validation.Heartbeat(req);
            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var now = Database.Ts(clock.GetUtcNow());
            using var c = db.Open();
            using var tx = c.BeginTransaction();
            foreach (var m in req.Marks ?? [])
            {
                c.Execute("""
                    INSERT INTO marks (asset, bid, ask, last, updated_at) VALUES (@asset, @Bid, @Ask, @Last, @now)
                    ON CONFLICT(asset) DO UPDATE SET bid = excluded.bid, ask = excluded.ask, last = excluded.last, updated_at = excluded.updated_at
                    """, new { asset = Normalize(m.Asset), m.Bid, m.Ask, m.Last, now }, tx);
            }
            c.Execute("""
                UPDATE trader_control SET last_seen_at = @now, mode = @Mode,
                    paper_starting_cash_usd = @PaperStartingCashUsd, cycle_seconds = @CycleSeconds
                WHERE id = 1
                """, new { now, req.Mode, req.PaperStartingCashUsd, req.CycleSeconds }, tx);
            tx.Commit();
            return Results.Ok(c.QuerySingle<TraderControl>(select));
        });
    }

    private static string Normalize(string asset) => asset.Trim().ToUpperInvariant();
}
