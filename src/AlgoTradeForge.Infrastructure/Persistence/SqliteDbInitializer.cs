using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.Infrastructure.Persistence;

internal static class SqliteDbInitializer
{
    private const int CurrentVersion = 18;

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

    private const string MigrationV3 = """
        ALTER TABLE backtest_runs ADD COLUMN error_message TEXT NULL;
        ALTER TABLE backtest_runs ADD COLUMN error_stack_trace TEXT NULL;
        """;

    private const string MigrationV4 = """
        ALTER TABLE optimization_runs ADD COLUMN filtered_trials INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE optimization_runs ADD COLUMN failed_trials INTEGER NOT NULL DEFAULT 0;
        """;

    private const string MigrationV6 = """
        ALTER TABLE backtest_runs ADD COLUMN trade_pnl_json TEXT NOT NULL DEFAULT '[]';
        """;

    private const string MigrationV7 = """
        ALTER TABLE optimization_runs ADD COLUMN optimization_method TEXT NULL;
        ALTER TABLE optimization_runs ADD COLUMN generations_completed INTEGER NULL;
        """;

    private const string MigrationV8 = """
        ALTER TABLE optimization_runs ADD COLUMN error_message TEXT NULL;
        """;

    private const string MigrationV9 = """
        ALTER TABLE optimization_runs ADD COLUMN status TEXT NOT NULL DEFAULT 'Completed';
        UPDATE optimization_runs SET status = 'InProgress' WHERE completed_at = '';
        UPDATE optimization_runs SET status = 'Cancelled' WHERE error_message = 'Run was cancelled by user.' AND completed_at != '';
        UPDATE optimization_runs SET status = 'Failed' WHERE error_message IS NOT NULL AND error_message != 'Run was cancelled by user.' AND completed_at != '';
        """;

    private const string MigrationV10 = """
        ALTER TABLE optimization_runs ADD COLUMN input_json TEXT NULL;
        """;

    private const string MigrationV11 = """
        ALTER TABLE backtest_runs ADD COLUMN fitness_score REAL NULL;
        """;

    private const string MigrationV12 = """
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
            error_message           TEXT    NULL
        );
        CREATE INDEX IF NOT EXISTS ix_validation_runs_opt_id ON validation_runs(optimization_run_id);

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

    private const string MigrationV14 = """
        CREATE TABLE IF NOT EXISTS threshold_profiles (
            name            TEXT    NOT NULL PRIMARY KEY,
            profile_json    TEXT    NOT NULL,
            is_builtin      INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT    NOT NULL,
            updated_at      TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS simulation_cache_metadata (
            optimization_run_id TEXT    NOT NULL PRIMARY KEY REFERENCES optimization_runs(id),
            bar_count           INTEGER NOT NULL,
            trial_count         INTEGER NOT NULL,
            cache_file_path     TEXT    NULL,
            created_at          TEXT    NOT NULL,
            size_bytes          INTEGER NOT NULL DEFAULT 0
        );
        """;

    private const string MigrationV16 = """
        ALTER TABLE backtest_runs ADD COLUMN subscriptions_json TEXT NULL;
        ALTER TABLE optimization_runs ADD COLUMN subscriptions_json TEXT NULL;
        """;

    private const string MigrationV17 = """
        CREATE INDEX IF NOT EXISTS ix_br_opt_fitness ON backtest_runs(optimization_run_id, fitness_score DESC);
        """;

    // V18: Per-DSS optimization split — drops all optimization/validation data (FR-014)
    // and recreates tables with new group structure + denormalized metrics.
    private const string MigrationV18 = """
        DROP TABLE IF EXISTS validation_stage_results;
        DROP TABLE IF EXISTS validation_runs;
        DROP TABLE IF EXISTS validation_groups;
        DROP TABLE IF EXISTS simulation_cache_metadata;
        DROP TABLE IF EXISTS optimization_failed_trials;
        DROP TABLE IF EXISTS backtest_runs;
        DROP TABLE IF EXISTS optimization_runs;
        DROP TABLE IF EXISTS optimization_groups;
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

        if (currentVersion < 6)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV6;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 6);
        }

        if (currentVersion < 7)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV7;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 7);
        }

        if (currentVersion < 8)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV8;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 8);
        }

        if (currentVersion < 9)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV9;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 9);
        }

        if (currentVersion < 10)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV10;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 10);
        }

        if (currentVersion < 11)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV11;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 11);
        }

        if (currentVersion < 12)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV12;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 12);
        }

        if (currentVersion < 13)
        {
            // Schema's CREATE TABLE IF NOT EXISTS may have already created
            // validation_runs with these columns (for DBs upgrading from < v12).
            // SQLite has no ADD COLUMN IF NOT EXISTS, so check PRAGMA first.
            await AddColumnIfNotExistsAsync(connection, "validation_runs", "category_scores_json", "TEXT NULL");
            await AddColumnIfNotExistsAsync(connection, "validation_runs", "rejections_json", "TEXT NULL");
            await SetVersionAsync(connection, 13);
        }

        if (currentVersion < 14)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV14;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 14);
        }

        if (currentVersion < 15)
        {
            await AddColumnIfNotExistsAsync(connection, "optimization_runs", "dedup_skipped", "INTEGER NOT NULL DEFAULT 0");
            await SetVersionAsync(connection, 15);
        }

        if (currentVersion < 16)
        {
            await using var migrateCmd = connection.CreateCommand();
            migrateCmd.CommandText = MigrationV16;
            await migrateCmd.ExecuteNonQueryAsync();
            await SetVersionAsync(connection, 16);
        }

        if (currentVersion < 17)
        {
            // Index created asynchronously by SqliteIndexMaintenanceService
            // to avoid blocking startup on large databases.
            await SetVersionAsync(connection, 17);
        }

        if (currentVersion < 18)
        {
            // Per-DSS optimization split: drop all optimization/validation data,
            // then re-run the base Schema to recreate tables with new structure.
            await using var dropCmd = connection.CreateCommand();
            dropCmd.CommandText = MigrationV18;
            await dropCmd.ExecuteNonQueryAsync();

            await using var recreateCmd = connection.CreateCommand();
            recreateCmd.CommandText = Schema;
            await recreateCmd.ExecuteNonQueryAsync();

            await SetVersionAsync(connection, 18);
        }

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
