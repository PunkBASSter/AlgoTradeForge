using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.Infrastructure.Persistence;

internal static class SqliteDbInitializer
{
    private const int CurrentVersion = 5;

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
            timeframe           TEXT    NOT NULL,
            filtered_trials     INTEGER NOT NULL DEFAULT 0,
            failed_trials       INTEGER NOT NULL DEFAULT 0
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
            timeframe           TEXT    NOT NULL,
            error_message       TEXT    NULL,
            error_stack_trace   TEXT    NULL
        );

        CREATE INDEX IF NOT EXISTS ix_br_strategy ON backtest_runs(strategy_name);
        CREATE INDEX IF NOT EXISTS ix_br_completed ON backtest_runs(completed_at);
        CREATE INDEX IF NOT EXISTS ix_br_opt_id ON backtest_runs(optimization_run_id);
        CREATE INDEX IF NOT EXISTS ix_br_asset ON backtest_runs(asset_name, exchange, timeframe);
        CREATE INDEX IF NOT EXISTS ix_opr_asset ON optimization_runs(asset_name, exchange, timeframe);

        CREATE TABLE IF NOT EXISTS optimization_failed_trials (
            id                     TEXT    NOT NULL PRIMARY KEY,
            optimization_run_id    TEXT    NOT NULL REFERENCES optimization_runs(id),
            exception_type         TEXT    NOT NULL,
            exception_message      TEXT    NOT NULL,
            stack_trace            TEXT    NOT NULL,
            sample_parameters_json TEXT    NOT NULL,
            occurrence_count       INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_oft_opt_id ON optimization_failed_trials(optimization_run_id);
        """;

    private const string MigrationV3 = """
        ALTER TABLE backtest_runs ADD COLUMN error_message TEXT NULL;
        ALTER TABLE backtest_runs ADD COLUMN error_stack_trace TEXT NULL;
        """;

    private const string MigrationV4 = """
        ALTER TABLE optimization_runs ADD COLUMN filtered_trials INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE optimization_runs ADD COLUMN failed_trials INTEGER NOT NULL DEFAULT 0;
        """;

    private const string MigrationV5 = """
        CREATE TABLE IF NOT EXISTS optimization_failed_trials (
            id                     TEXT    NOT NULL PRIMARY KEY,
            optimization_run_id    TEXT    NOT NULL REFERENCES optimization_runs(id),
            exception_type         TEXT    NOT NULL,
            exception_message      TEXT    NOT NULL,
            stack_trace            TEXT    NOT NULL,
            sample_parameters_json TEXT    NOT NULL,
            occurrence_count       INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_oft_opt_id ON optimization_failed_trials(optimization_run_id);
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

        // Apply migrations for existing databases
        var currentVersion = await GetVersionAsync(connection);
        if (currentVersion < 3)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV3;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 3);
        }

        if (currentVersion < 4)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV4;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 4);
        }

        if (currentVersion < 5)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV5;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 5);
        }
    }

    private static async Task<int> GetVersionAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        var result = await cmd.ExecuteScalarAsync();
        return result is not null ? Convert.ToInt32(result) : 0;
    }

    private static async Task SetVersionAsync(SqliteConnection connection, int version)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE schema_version SET version = $version";
        cmd.Parameters.AddWithValue("$version", version);
        await cmd.ExecuteNonQueryAsync();
    }
}
