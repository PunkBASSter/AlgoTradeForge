using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.Infrastructure.Persistence;

internal static class SqliteDbInitializer
{
    private const string Schema = """
        PRAGMA journal_mode=WAL;

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
            max_parallelism     INTEGER NOT NULL
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
            optimization_run_id TEXT    NULL REFERENCES optimization_runs(id)
        );

        CREATE TABLE IF NOT EXISTS backtest_data_subscriptions (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            backtest_run_id TEXT NOT NULL REFERENCES backtest_runs(id) ON DELETE CASCADE,
            asset_name      TEXT NOT NULL,
            exchange        TEXT NOT NULL,
            timeframe       TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS optimization_data_subscriptions (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            optimization_run_id TEXT NOT NULL REFERENCES optimization_runs(id) ON DELETE CASCADE,
            asset_name          TEXT NOT NULL,
            exchange            TEXT NOT NULL,
            timeframe           TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_br_strategy ON backtest_runs(strategy_name);
        CREATE INDEX IF NOT EXISTS ix_br_completed ON backtest_runs(completed_at);
        CREATE INDEX IF NOT EXISTS ix_br_opt_id ON backtest_runs(optimization_run_id);
        CREATE INDEX IF NOT EXISTS ix_bds_asset ON backtest_data_subscriptions(asset_name, exchange);
        CREATE INDEX IF NOT EXISTS ix_bds_tf ON backtest_data_subscriptions(timeframe);
        CREATE INDEX IF NOT EXISTS ix_bds_run ON backtest_data_subscriptions(backtest_run_id);
        CREATE INDEX IF NOT EXISTS ix_ods_asset ON optimization_data_subscriptions(asset_name, exchange);
        CREATE INDEX IF NOT EXISTS ix_ods_run ON optimization_data_subscriptions(optimization_run_id);
        """;

    public static async Task EnsureCreatedAsync(string connectionString)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync();
    }
}
