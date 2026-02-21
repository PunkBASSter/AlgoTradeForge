using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.Infrastructure.Persistence;

internal static class SqliteDbInitializer
{
    private const int CurrentVersion = 2;

    private const string Schema = """
        PRAGMA journal_mode=WAL;

        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS optimization_runs (
            id                  TEXT    NOT NULL PRIMARY KEY,
            strategy_name       TEXT    NOT NULL,
            strategy_version    TEXT    NOT NULL,
            started_at          TEXT    NOT NULL,
            completed_at        TEXT    NOT NULL,
            duration_ms         INTEGER NOT NULL,
            total_combinations  INTEGER NOT NULL,
            sort_by             TEXT    NOT NULL,
            data_start          TEXT    NOT NULL,
            data_end            TEXT    NOT NULL,
            initial_cash        TEXT    NOT NULL,
            commission          TEXT    NOT NULL,
            slippage_ticks      INTEGER NOT NULL,
            max_parallelism     INTEGER NOT NULL,
            asset_name          TEXT    NOT NULL,
            exchange            TEXT    NOT NULL,
            timeframe           TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS backtest_runs (
            id                  TEXT    NOT NULL PRIMARY KEY,
            strategy_name       TEXT    NOT NULL,
            strategy_version    TEXT    NOT NULL,
            parameters_json     TEXT    NOT NULL,
            initial_cash        TEXT    NOT NULL,
            commission          TEXT    NOT NULL,
            slippage_ticks      INTEGER NOT NULL,
            started_at          TEXT    NOT NULL,
            completed_at        TEXT    NOT NULL,
            data_start          TEXT    NOT NULL,
            data_end            TEXT    NOT NULL,
            duration_ms         INTEGER NOT NULL,
            total_bars          INTEGER NOT NULL,
            metrics_json        TEXT    NOT NULL,
            equity_curve_json   TEXT    NOT NULL,
            run_folder_path     TEXT    NULL,
            run_mode            TEXT    NOT NULL DEFAULT 'Backtest',
            optimization_run_id TEXT    NULL REFERENCES optimization_runs(id),
            asset_name          TEXT    NOT NULL,
            exchange            TEXT    NOT NULL,
            timeframe           TEXT    NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_br_strategy ON backtest_runs(strategy_name);
        CREATE INDEX IF NOT EXISTS ix_br_completed ON backtest_runs(completed_at);
        CREATE INDEX IF NOT EXISTS ix_br_opt_id ON backtest_runs(optimization_run_id);
        CREATE INDEX IF NOT EXISTS ix_br_asset ON backtest_runs(asset_name, exchange, timeframe);
        CREATE INDEX IF NOT EXISTS ix_opr_asset ON optimization_runs(asset_name, exchange, timeframe);
        """;

    public static async Task EnsureCreatedAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var schemaCmd = connection.CreateCommand();
        schemaCmd.CommandText = Schema;
        await schemaCmd.ExecuteNonQueryAsync();

        // Seed version on first run; future migrations will check and increment
        await using var versionCmd = connection.CreateCommand();
        versionCmd.CommandText = $"""
            INSERT INTO schema_version (version)
            SELECT {CurrentVersion}
            WHERE NOT EXISTS (SELECT 1 FROM schema_version)
            """;
        await versionCmd.ExecuteNonQueryAsync();
    }
}
