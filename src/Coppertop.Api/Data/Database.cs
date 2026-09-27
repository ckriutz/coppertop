using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Coppertop.Api.Data;

public sealed class Database
{
    private readonly string _connectionString;

    static Database()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
        SqlMapper.AddTypeHandler(new NullableDateTimeOffsetHandler());
    }

    public Database(string connectionString) => _connectionString = connectionString;

    // Timestamps are stored as fixed-width UTC ISO-8601 text so SQL string comparison matches time order.
    public static string Ts(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    public static string? Ts(DateTimeOffset? value) => value is null ? null : Ts(value.Value);

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;");
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        connection.Execute("PRAGMA journal_mode = WAL;");
        connection.Execute(Schema);
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS opportunities (
            id               INTEGER PRIMARY KEY AUTOINCREMENT,
            asset            TEXT    NOT NULL,
            strategy         TEXT    NOT NULL,
            max_entry_price  REAL    NOT NULL,
            take_profit_pct  REAL    NOT NULL,
            stop_loss_pct    REAL    NOT NULL,
            max_spend_usd    REAL    NOT NULL,
            confidence       REAL    NOT NULL,
            reason           TEXT    NOT NULL,
            status           TEXT    NOT NULL,
            created_at       TEXT    NOT NULL,
            expires_at       TEXT    NOT NULL,
            closed_at        TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS ix_opportunities_status ON opportunities(status, asset);

        CREATE TABLE IF NOT EXISTS vetoes (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            asset       TEXT NOT NULL,
            reason      TEXT NOT NULL,
            created_at  TEXT NOT NULL,
            expires_at  TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_vetoes_asset ON vetoes(asset, expires_at);

        CREATE TABLE IF NOT EXISTS positions (
            id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            opportunity_id     INTEGER NULL REFERENCES opportunities(id),
            asset              TEXT    NOT NULL,
            volume             REAL    NOT NULL,
            entry_price        REAL    NOT NULL,
            entry_fee_usd      REAL    NOT NULL,
            cost_usd           REAL    NOT NULL,
            take_profit_price  REAL    NOT NULL,
            stop_loss_price    REAL    NOT NULL,
            status             TEXT    NOT NULL,
            is_simulated       INTEGER NOT NULL,
            opened_at          TEXT    NOT NULL,
            closed_at          TEXT    NULL,
            exit_price         REAL    NULL,
            exit_fee_usd       REAL    NULL,
            realized_pnl_usd   REAL    NULL,
            close_reason       TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS ix_positions_status ON positions(status, asset);

        CREATE TABLE IF NOT EXISTS orders (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            opportunity_id  INTEGER NULL REFERENCES opportunities(id),
            position_id     INTEGER NULL REFERENCES positions(id),
            asset           TEXT    NOT NULL,
            side            TEXT    NOT NULL,
            order_type      TEXT    NOT NULL,
            purpose         TEXT    NOT NULL,
            price           REAL    NOT NULL,
            volume          REAL    NOT NULL,
            status          TEXT    NOT NULL,
            kraken_tx_id    TEXT    NULL,
            is_simulated    INTEGER NOT NULL,
            note            TEXT    NULL,
            created_at      TEXT    NOT NULL,
            updated_at      TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_orders_status ON orders(status);

        CREATE TABLE IF NOT EXISTS trades (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            order_id      INTEGER NULL REFERENCES orders(id),
            position_id   INTEGER NULL REFERENCES positions(id),
            asset         TEXT    NOT NULL,
            side          TEXT    NOT NULL,
            price         REAL    NOT NULL,
            volume        REAL    NOT NULL,
            fee_usd       REAL    NOT NULL,
            is_simulated  INTEGER NOT NULL,
            executed_at   TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS token_usage (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            service        TEXT    NOT NULL,
            model          TEXT    NOT NULL,
            purpose        TEXT    NOT NULL,
            input_tokens   INTEGER NOT NULL,
            output_tokens  INTEGER NOT NULL,
            cost_usd       REAL    NOT NULL,
            created_at     TEXT    NOT NULL
        );
        """;

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) => parameter.Value = Ts(value);

        public override DateTimeOffset Parse(object value) =>
            DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private sealed class NullableDateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset?>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset? value) =>
            parameter.Value = (object?)Ts(value) ?? DBNull.Value;

        public override DateTimeOffset? Parse(object value) =>
            value is null or DBNull
                ? null
                : DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }
}
