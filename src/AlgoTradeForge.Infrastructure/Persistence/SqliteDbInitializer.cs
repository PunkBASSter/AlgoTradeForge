using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.Infrastructure.Persistence;

internal static class SqliteDbInitializer
{
    private const int CurrentVersion = 1;

    private static readonly SemaphoreSlim _orphanCleanupLock = new(1, 1);
    private static bool _orphanCleanupDone;

    private const string Schema = """
        PRAGMA journal_mode=WAL;

        CREATE TABLE IF NOT EXISTS schema_version (
            version INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS optimization_groups (
            id                          TEXT    NOT NULL PRIMARY KEY,
            strategy_name               TEXT    NOT NULL,
            strategy_version            TEXT    NULL,
            optimization_method         TEXT    NOT NULL,
            started_at                  TEXT    NOT NULL,
            completed_at                TEXT    NULL,
            total_runs                  INTEGER NOT NULL,
            status                      TEXT    NOT NULL DEFAULT 'InProgress',
            input_json                  TEXT    NULL,
            subscriptions_json          TEXT    NOT NULL,
            backtest_settings_json      TEXT    NOT NULL,
            optimization_settings_json  TEXT    NULL,
            fitness_config_json         TEXT    NULL,
            max_parallelism             INTEGER NOT NULL
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
            failed_trials       INTEGER NOT NULL DEFAULT 0,
            dedup_skipped       INTEGER NOT NULL DEFAULT 0,
            optimization_method TEXT    NULL,
            generations_completed INTEGER NULL,
            input_json          TEXT    NULL,
            error_message       TEXT    NULL,
            status              TEXT    NOT NULL DEFAULT 'Completed',
            subscriptions_json  TEXT    NULL,
            group_id            TEXT    NULL REFERENCES optimization_groups(id),
            dss_index           INTEGER NOT NULL DEFAULT 0
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
            trade_pnl_json      TEXT    NOT NULL DEFAULT '[]',
            run_folder_path     TEXT    NULL,
            run_mode            TEXT    NOT NULL DEFAULT 'Backtest',
            optimization_run_id TEXT    NULL REFERENCES optimization_runs(id),
            asset_name          TEXT    NOT NULL,
            exchange            TEXT    NOT NULL,
            timeframe           TEXT    NOT NULL,
            error_message       TEXT    NULL,
            error_stack_trace   TEXT    NULL,
            fitness_score       REAL    NULL,
            subscriptions_json  TEXT    NULL,
            sharpe_ratio        REAL    NULL,
            sortino_ratio       REAL    NULL,
            profit_factor       REAL    NULL,
            max_drawdown_pct    REAL    NULL,
            win_rate_pct        REAL    NULL,
            total_trades        INTEGER NULL,
            net_profit          REAL    NULL,
            annualized_return_pct REAL  NULL
        );

        CREATE INDEX IF NOT EXISTS ix_br_strategy ON backtest_runs(strategy_name);
        CREATE INDEX IF NOT EXISTS ix_br_completed ON backtest_runs(completed_at);
        CREATE INDEX IF NOT EXISTS ix_br_opt_id ON backtest_runs(optimization_run_id);
        CREATE INDEX IF NOT EXISTS ix_br_asset ON backtest_runs(asset_name, exchange, timeframe);
        CREATE INDEX IF NOT EXISTS ix_opr_asset ON optimization_runs(asset_name, exchange, timeframe);
        CREATE INDEX IF NOT EXISTS ix_or_group_id ON optimization_runs(group_id);
        -- ix_br_opt_fitness created asynchronously by SqliteIndexMaintenanceService

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

        CREATE TABLE IF NOT EXISTS validation_groups (
            id                      TEXT    NOT NULL PRIMARY KEY,
            optimization_group_id   TEXT    NOT NULL REFERENCES optimization_groups(id),
            strategy_name           TEXT    NOT NULL,
            threshold_profile_name  TEXT    NOT NULL,
            threshold_profile_json  TEXT    NULL,
            started_at              TEXT    NOT NULL,
            completed_at            TEXT    NULL,
            total_runs              INTEGER NOT NULL,
            status                  TEXT    NOT NULL DEFAULT 'InProgress'
        );
        CREATE INDEX IF NOT EXISTS ix_vg_opt_group_id ON validation_groups(optimization_group_id);

        CREATE TABLE IF NOT EXISTS validation_runs (
            id                      TEXT    NOT NULL PRIMARY KEY,
            optimization_run_id     TEXT    NOT NULL REFERENCES optimization_runs(id),
            strategy_name           TEXT    NOT NULL,
            strategy_version        TEXT    NULL,
            started_at              TEXT    NOT NULL,
            completed_at            TEXT    NULL,
            duration_ms             INTEGER NOT NULL DEFAULT 0,
            status                  TEXT    NOT NULL DEFAULT 'InProgress',
            threshold_profile_name  TEXT    NOT NULL,
            threshold_profile_json  TEXT    NULL,
            candidates_in           INTEGER NOT NULL DEFAULT 0,
            candidates_out          INTEGER NOT NULL DEFAULT 0,
            composite_score         REAL    NOT NULL DEFAULT 0,
            verdict                 TEXT    NOT NULL DEFAULT 'Red',
            verdict_summary         TEXT    NULL,
            invocation_count        INTEGER NOT NULL DEFAULT 1,
            error_message           TEXT    NULL,
            category_scores_json    TEXT    NULL,
            rejections_json         TEXT    NULL,
            validation_group_id     TEXT    NULL REFERENCES validation_groups(id)
        );
        CREATE INDEX IF NOT EXISTS ix_validation_runs_opt_id ON validation_runs(optimization_run_id);
        CREATE INDEX IF NOT EXISTS ix_vr_validation_group_id ON validation_runs(validation_group_id);

        CREATE TABLE IF NOT EXISTS validation_stage_results (
            id                      INTEGER PRIMARY KEY AUTOINCREMENT,
            validation_run_id       TEXT    NOT NULL REFERENCES validation_runs(id),
            stage_number            INTEGER NOT NULL,
            stage_name              TEXT    NOT NULL,
            candidates_in           INTEGER NOT NULL DEFAULT 0,
            candidates_out          INTEGER NOT NULL DEFAULT 0,
            duration_ms             INTEGER NOT NULL DEFAULT 0,
            candidate_verdicts_json TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS ix_vsr_validation_run_id ON validation_stage_results(validation_run_id);

        CREATE TABLE IF NOT EXISTS simulation_cache_metadata (
            optimization_run_id TEXT    NOT NULL PRIMARY KEY REFERENCES optimization_runs(id),
            bar_count           INTEGER NOT NULL,
            trial_count         INTEGER NOT NULL,
            cache_file_path     TEXT    NULL,
            created_at          TEXT    NOT NULL,
            size_bytes          INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE IF NOT EXISTS threshold_profiles (
            name            TEXT    NOT NULL PRIMARY KEY,
            profile_json    TEXT    NOT NULL,
            is_builtin      INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT    NOT NULL,
            updated_at      TEXT    NOT NULL
        );
        """;

    // Migrations removed — Schema represents the canonical v1 state.
    // Future migrations should be added as MigrationV2, V3, etc.

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

        // Future migrations go here as: if (currentVersion < 2) { ... }

        await CleanupOrphanedRunsOnceAsync(connection);
    }

    /// <summary>
    /// Marks orphaned in-progress runs as failed. Guarded by a static flag so it
    /// runs exactly once per process, even though multiple repositories each call
    /// <see cref="EnsureCreatedAsync"/> independently with their own init flags.
    /// </summary>
    private static async Task CleanupOrphanedRunsOnceAsync(SqliteConnection connection)
    {
        if (Volatile.Read(ref _orphanCleanupDone))
            return;

        await _orphanCleanupLock.WaitAsync();
        try
        {
            if (_orphanCleanupDone)
                return;

            await using var orphanCmd = connection.CreateCommand();
            orphanCmd.CommandText = """
                UPDATE optimization_runs
                SET completed_at = started_at, error_message = 'Server restarted during execution', status = 'Failed'
                WHERE status IN ('InProgress', 'Enqueued')
                """;
            await orphanCmd.ExecuteNonQueryAsync();

            await using var orphanValCmd = connection.CreateCommand();
            orphanValCmd.CommandText = """
                UPDATE validation_runs
                SET completed_at = started_at, error_message = 'Server restarted during execution', status = 'Failed'
                WHERE status IN ('InProgress', 'Enqueued')
                """;
            await orphanValCmd.ExecuteNonQueryAsync();

            await using var orphanOptGroupCmd = connection.CreateCommand();
            orphanOptGroupCmd.CommandText = """
                UPDATE optimization_groups SET status = 'Failed', completed_at = started_at
                WHERE status = 'InProgress'
                """;
            await orphanOptGroupCmd.ExecuteNonQueryAsync();

            await using var orphanValGroupCmd = connection.CreateCommand();
            orphanValGroupCmd.CommandText = """
                UPDATE validation_groups SET status = 'Failed', completed_at = started_at
                WHERE status = 'InProgress'
                """;
            await orphanValGroupCmd.ExecuteNonQueryAsync();

            _orphanCleanupDone = true;
        }
        finally
        {
            _orphanCleanupLock.Release();
        }
    }

    private static async Task<int> GetVersionAsync(SqliteConnection connection)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT version FROM schema_version LIMIT 1";
        var result = await cmd.ExecuteScalarAsync();
        return result is not null ? Convert.ToInt32(result) : 0;
    }

    private static readonly System.Text.RegularExpressions.Regex SafeIdentifier =
        new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static async Task AddColumnIfNotExistsAsync(
        SqliteConnection connection, string table, string column, string definition)
    {
        if (!SafeIdentifier.IsMatch(table))
            throw new ArgumentException($"Invalid table identifier: {table}", nameof(table));
        if (!SafeIdentifier.IsMatch(column))
            throw new ArgumentException($"Invalid column identifier: {column}", nameof(column));

        await using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $col";
        checkCmd.Parameters.AddWithValue("$col", column);
        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;
        if (exists) return;

        // table/column are validated against SafeIdentifier above; definition is always a hardcoded literal.
        await using var alterCmd = connection.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE [{table}] ADD COLUMN [{column}] {definition}";
        await alterCmd.ExecuteNonQueryAsync();
    }

    private static async Task SetVersionAsync(SqliteConnection connection, int version)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE schema_version SET version = $version";
        cmd.Parameters.AddWithValue("$version", version);
        await cmd.ExecuteNonQueryAsync();
    }
}
