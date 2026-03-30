using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Reporting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Persistence;

public sealed class SqliteRunRepository : IRunRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>All backtest_runs columns except equity_curve_json, for list queries.</summary>
    private const string BacktestListColumns = """
        id, strategy_name, strategy_version, parameters_json,
        initial_cash, commission, slippage_ticks,
        started_at, completed_at, data_start, data_end,
        duration_ms, total_bars, metrics_json,
        run_folder_path, run_mode, optimization_run_id,
        asset_name, exchange, timeframe,
        error_message, error_stack_trace, fitness_score
        """;

    public SqliteRunRepository(IOptions<RunStorageOptions> options)
    {
        var dbPath = options.Value.DatabasePath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
    }

    public void Dispose() => _initLock.Dispose();

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _initialized))
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (!_initialized)
            {
                await SqliteDbInitializer.EnsureCreatedAsync(_connectionString);
                _initialized = true;
            }
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<SqliteConnection> CreateConnectionAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ── Save backtest ──────────────────────────────────────────────────

    public async Task SaveAsync(BacktestRunRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);
        using var tx = conn.BeginTransaction();

        await InsertBacktestRunAsync(conn, tx, record, ct);

        tx.Commit();
    }

    private static async Task InsertBacktestRunAsync(
        SqliteConnection conn, SqliteTransaction tx, BacktestRunRecord r, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO backtest_runs (
                id, strategy_name, strategy_version, parameters_json,
                initial_cash, commission, slippage_ticks,
                started_at, completed_at, data_start, data_end,
                duration_ms, total_bars, metrics_json, equity_curve_json,
                trade_pnl_json,
                run_folder_path, run_mode, optimization_run_id,
                asset_name, exchange, timeframe,
                error_message, error_stack_trace, fitness_score
            ) VALUES (
                $id, $stratName, $stratVer, $paramsJson,
                $cash, $commission, $slippage,
                $startedAt, $completedAt, $dataStart, $dataEnd,
                $durationMs, $totalBars, $metricsJson, $equityJson,
                $tradePnlJson,
                $runFolder, $runMode, $optId,
                $asset, $exchange, $tf,
                $errorMsg, $errorStack, $fitnessScore
            )
            """;

        cmd.Parameters.AddWithValue("$id", r.Id.ToString());
        cmd.Parameters.AddWithValue("$stratName", r.StrategyName);
        cmd.Parameters.AddWithValue("$stratVer", r.StrategyVersion);
        cmd.Parameters.AddWithValue("$paramsJson", JsonSerializer.Serialize(r.Parameters, JsonOptions));
        cmd.Parameters.AddWithValue("$cash", r.BacktestSettings.InitialCash.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$commission", r.BacktestSettings.CommissionPerTrade.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$slippage", r.BacktestSettings.SlippageTicks);
        cmd.Parameters.AddWithValue("$startedAt", r.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completedAt", r.CompletedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$dataStart", r.BacktestSettings.StartTime.ToString("O"));
        cmd.Parameters.AddWithValue("$dataEnd", r.BacktestSettings.EndTime.ToString("O"));
        cmd.Parameters.AddWithValue("$durationMs", r.DurationMs);
        cmd.Parameters.AddWithValue("$totalBars", r.TotalBars);
        cmd.Parameters.AddWithValue("$metricsJson", JsonSerializer.Serialize(r.Metrics, JsonOptions));
        cmd.Parameters.AddWithValue("$equityJson", SerializeEquityCurve(r.EquityCurve));
        cmd.Parameters.AddWithValue("$tradePnlJson", SerializeTradePnl(r.TradePnl));
        cmd.Parameters.AddWithValue("$runFolder", (object?)r.RunFolderPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$runMode", r.RunMode);
        cmd.Parameters.AddWithValue("$optId", r.OptimizationRunId.HasValue ? r.OptimizationRunId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$asset", r.DataSubscription.AssetName);
        cmd.Parameters.AddWithValue("$exchange", r.DataSubscription.Exchange);
        cmd.Parameters.AddWithValue("$tf", r.DataSubscription.TimeFrame);
        cmd.Parameters.AddWithValue("$errorMsg", (object?)r.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errorStack", (object?)r.ErrorStackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fitnessScore",
            r.FitnessScore is { } fs && double.IsFinite(fs) ? fs : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Get backtest by ID ─────────────────────────────────────────────

    public async Task<BacktestRunRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM backtest_runs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadBacktestRunCore(reader, includeEquityCurve: true);
    }

    // ── Query backtests ────────────────────────────────────────────────

    public async Task<PagedResult<BacktestRunRecord>> QueryAsync(BacktestRunQuery query, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        var parameters = new List<SqliteParameter>();
        var conditions = new List<string>();

        if (query.StrategyName is not null)
        {
            conditions.Add("br.strategy_name = $stratName");
            parameters.Add(new SqliteParameter("$stratName", query.StrategyName));
        }
        if (query.AssetName is not null)
        {
            conditions.Add("br.asset_name = $asset");
            parameters.Add(new SqliteParameter("$asset", query.AssetName));
        }
        if (query.Exchange is not null)
        {
            conditions.Add("br.exchange = $exchange");
            parameters.Add(new SqliteParameter("$exchange", query.Exchange));
        }
        if (query.TimeFrame is not null)
        {
            conditions.Add("br.timeframe = $tf");
            parameters.Add(new SqliteParameter("$tf", query.TimeFrame));
        }
        if (query.StandaloneOnly == true)
        {
            conditions.Add("br.optimization_run_id IS NULL");
        }
        if (query.From is not null)
        {
            conditions.Add("br.completed_at >= $from");
            parameters.Add(new SqliteParameter("$from", query.From.Value.ToString("O")));
        }
        if (query.To is not null)
        {
            conditions.Add("br.completed_at <= $to");
            parameters.Add(new SqliteParameter("$to", query.To.Value.ToString("O")));
        }

        var whereClause = conditions.Count > 0
            ? " WHERE " + string.Join(" AND ", conditions)
            : "";

        // Count total matching rows
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM backtest_runs br{whereClause}";
        foreach (var p in parameters)
            countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // Fetch page
        var sb = new StringBuilder($"SELECT {BacktestListColumns} FROM backtest_runs br");
        sb.Append(whereClause);
        sb.Append(" ORDER BY br.completed_at DESC");
        sb.Append(" LIMIT $limit OFFSET $offset");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        foreach (var p in parameters)
            cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        cmd.Parameters.Add(new SqliteParameter("$limit", query.Limit));
        cmd.Parameters.Add(new SqliteParameter("$offset", query.Offset));

        var results = new List<BacktestRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadBacktestRunCore(reader, includeEquityCurve: false));

        return new PagedResult<BacktestRunRecord>(results, totalCount);
    }

    // ── Insert optimization placeholder ───────────────────────────────

    public async Task InsertOptimizationPlaceholderAsync(OptimizationRunRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO optimization_runs (
                id, strategy_name, strategy_version,
                started_at, completed_at, duration_ms, total_combinations,
                sort_by, data_start, data_end,
                initial_cash, commission, slippage_ticks, max_parallelism,
                asset_name, exchange, timeframe, filtered_trials, failed_trials,
                optimization_method, generations_completed, input_json, error_message, status
            ) VALUES (
                $id, $stratName, $stratVer,
                $startedAt, '', 0, $totalCombinations,
                $sortBy, $dataStart, $dataEnd,
                $cash, $commission, $slippage, $maxParallelism,
                $asset, $exchange, $tf, 0, 0,
                $optMethod, NULL, $inputJson, NULL, 'InProgress'
            )
            """;

        cmd.Parameters.AddWithValue("$id", record.Id.ToString());
        cmd.Parameters.AddWithValue("$stratName", record.StrategyName);
        cmd.Parameters.AddWithValue("$stratVer", record.StrategyVersion);
        cmd.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$totalCombinations", record.TotalCombinations);
        cmd.Parameters.AddWithValue("$sortBy", record.SortBy);
        cmd.Parameters.AddWithValue("$dataStart", record.BacktestSettings.StartTime.ToString("O"));
        cmd.Parameters.AddWithValue("$dataEnd", record.BacktestSettings.EndTime.ToString("O"));
        cmd.Parameters.AddWithValue("$cash", record.BacktestSettings.InitialCash.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$commission", record.BacktestSettings.CommissionPerTrade.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$slippage", record.BacktestSettings.SlippageTicks);
        cmd.Parameters.AddWithValue("$maxParallelism", record.MaxParallelism);
        cmd.Parameters.AddWithValue("$asset", record.DataSubscription.AssetName);
        cmd.Parameters.AddWithValue("$exchange", record.DataSubscription.Exchange);
        cmd.Parameters.AddWithValue("$tf", record.DataSubscription.TimeFrame);
        cmd.Parameters.AddWithValue("$optMethod", (object?)record.OptimizationMethod ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$inputJson", (object?)record.InputJson ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Save optimization ──────────────────────────────────────────────

    public async Task SaveOptimizationAsync(OptimizationRunRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);
        using var tx = conn.BeginTransaction();

        // Update parent optimization run (placeholder row already exists)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE optimization_runs SET
                    strategy_version = $stratVer,
                    completed_at = $completedAt,
                    duration_ms = $durationMs,
                    total_combinations = $totalCombinations,
                    filtered_trials = $filteredTrials,
                    failed_trials = $failedTrials,
                    error_message = $errorMsg,
                    optimization_method = $optMethod,
                    generations_completed = $gensCompleted,
                    status = $status
                WHERE id = $id
                """;

            cmd.Parameters.AddWithValue("$id", record.Id.ToString());
            cmd.Parameters.AddWithValue("$stratVer", record.StrategyVersion);
            cmd.Parameters.AddWithValue("$completedAt", record.CompletedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$durationMs", record.DurationMs);
            cmd.Parameters.AddWithValue("$totalCombinations", record.TotalCombinations);
            cmd.Parameters.AddWithValue("$filteredTrials", record.FilteredTrials);
            cmd.Parameters.AddWithValue("$failedTrials", record.FailedTrials);
            cmd.Parameters.AddWithValue("$errorMsg", (object?)record.ErrorMessage ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$optMethod", (object?)record.OptimizationMethod ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$gensCompleted", record.GenerationsCompleted.HasValue ? record.GenerationsCompleted.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("$status", record.Status);

            var rowsAffected = await cmd.ExecuteNonQueryAsync(ct);
            if (rowsAffected == 0)
                throw new InvalidOperationException($"Optimization placeholder row not found for id '{record.Id}'. Was InsertOptimizationPlaceholderAsync called first?");
        }

        // Insert child trial backtest runs
        foreach (var trial in record.Trials)
        {
            await InsertBacktestRunAsync(conn, tx, trial, ct);
        }

        // Insert failed trial details
        foreach (var failure in record.FailedTrialDetails)
        {
            await InsertFailedTrialAsync(conn, tx, failure, ct);
        }

        tx.Commit();
    }

    private static async Task InsertFailedTrialAsync(
        SqliteConnection conn, SqliteTransaction tx, FailedTrialRecord r, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO optimization_failed_trials (
                id, optimization_run_id, exception_type, exception_message,
                stack_trace, sample_parameters_json, occurrence_count
            ) VALUES (
                $id, $optId, $exType, $exMsg,
                $stack, $paramsJson, $count
            )
            """;

        cmd.Parameters.AddWithValue("$id", r.Id.ToString());
        cmd.Parameters.AddWithValue("$optId", r.OptimizationRunId.ToString());
        cmd.Parameters.AddWithValue("$exType", r.ExceptionType);
        cmd.Parameters.AddWithValue("$exMsg", r.ExceptionMessage);
        cmd.Parameters.AddWithValue("$stack", r.StackTrace);
        cmd.Parameters.AddWithValue("$paramsJson", JsonSerializer.Serialize(r.SampleParameters, JsonOptions));
        cmd.Parameters.AddWithValue("$count", r.OccurrenceCount);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Get optimization by ID ─────────────────────────────────────────

    public Task<OptimizationRunRecord?> GetOptimizationByIdAsync(Guid id, CancellationToken ct = default)
        => GetOptimizationByIdAsync(id, includeEquityCurves: false, ct);

    public async Task<OptimizationRunRecord?> GetOptimizationByIdAsync(Guid id, bool includeEquityCurves, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        OptimizationRunRecord record;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM optimization_runs WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            record = ReadOptimizationRun(reader);
        }

        // Load child trials sorted by the optimization's sort metric
        var trials = new List<BacktestRunRecord>();
        await using (var trialCmd = conn.CreateCommand())
        {
            var orderClause = GetTrialOrderByClause(record.SortBy);
            trialCmd.CommandText = $"SELECT * FROM backtest_runs WHERE optimization_run_id = $optId{orderClause}";
            trialCmd.Parameters.AddWithValue("$optId", id.ToString());

            await using var trialReader = await trialCmd.ExecuteReaderAsync(ct);
            while (await trialReader.ReadAsync(ct))
                trials.Add(ReadBacktestRunCore(trialReader, includeEquityCurve: includeEquityCurves));
        }

        // Load failed trial details
        var failedDetails = new List<FailedTrialRecord>();
        await using (var failedCmd = conn.CreateCommand())
        {
            failedCmd.CommandText = """
                SELECT * FROM optimization_failed_trials
                WHERE optimization_run_id = $optId
                ORDER BY occurrence_count DESC
                """;
            failedCmd.Parameters.AddWithValue("$optId", id.ToString());

            await using var failedReader = await failedCmd.ExecuteReaderAsync(ct);
            while (await failedReader.ReadAsync(ct))
                failedDetails.Add(ReadFailedTrial(failedReader));
        }

        return record with { Trials = trials, FailedTrialDetails = failedDetails };
    }

    private static FailedTrialRecord ReadFailedTrial(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        OptimizationRunId = Guid.Parse(reader.GetString(reader.GetOrdinal("optimization_run_id"))),
        ExceptionType = reader.GetString(reader.GetOrdinal("exception_type")),
        ExceptionMessage = reader.GetString(reader.GetOrdinal("exception_message")),
        StackTrace = reader.GetString(reader.GetOrdinal("stack_trace")),
        SampleParameters = DeserializeParameters(reader.GetString(reader.GetOrdinal("sample_parameters_json"))),
        OccurrenceCount = reader.GetInt64(reader.GetOrdinal("occurrence_count")),
    };

    // ── Query optimizations ────────────────────────────────────────────

    public async Task<PagedResult<OptimizationRunRecord>> QueryOptimizationsAsync(
        OptimizationRunQuery query, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        var parameters = new List<SqliteParameter>();
        var conditions = new List<string>();

        if (query.StrategyName is not null)
        {
            conditions.Add("opr.strategy_name = $stratName");
            parameters.Add(new SqliteParameter("$stratName", query.StrategyName));
        }
        if (query.AssetName is not null)
        {
            conditions.Add("opr.asset_name = $asset");
            parameters.Add(new SqliteParameter("$asset", query.AssetName));
        }
        if (query.Exchange is not null)
        {
            conditions.Add("opr.exchange = $exchange");
            parameters.Add(new SqliteParameter("$exchange", query.Exchange));
        }
        if (query.TimeFrame is not null)
        {
            conditions.Add("opr.timeframe = $tf");
            parameters.Add(new SqliteParameter("$tf", query.TimeFrame));
        }
        if (query.From is not null)
        {
            conditions.Add("opr.started_at >= $from");
            parameters.Add(new SqliteParameter("$from", query.From.Value.ToString("O")));
        }
        if (query.To is not null)
        {
            conditions.Add("opr.started_at <= $to");
            parameters.Add(new SqliteParameter("$to", query.To.Value.ToString("O")));
        }

        var whereClause = conditions.Count > 0
            ? " WHERE " + string.Join(" AND ", conditions)
            : "";

        // Count total matching rows
        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM optimization_runs opr{whereClause}";
        foreach (var p in parameters)
            countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // Fetch page — pin in-progress runs at top, then sort by start time
        var sb = new StringBuilder("SELECT * FROM optimization_runs opr");
        sb.Append(whereClause);
        sb.Append(" ORDER BY CASE WHEN opr.status = 'InProgress' THEN 0 ELSE 1 END, opr.started_at DESC");
        sb.Append(" LIMIT $limit OFFSET $offset");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        foreach (var p in parameters)
            cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        cmd.Parameters.Add(new SqliteParameter("$limit", query.Limit));
        cmd.Parameters.Add(new SqliteParameter("$offset", query.Offset));

        var results = new List<OptimizationRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadOptimizationRun(reader));

        return new PagedResult<OptimizationRunRecord>(results, totalCount);
    }

    // ── Delete optimization (cascade) ───────────────────────────────────

    public async Task<bool> DeleteOptimizationAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);
        using var tx = conn.BeginTransaction();

        var idStr = id.ToString();

        // Delete failed trial details
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM optimization_failed_trials WHERE optimization_run_id = $id";
            cmd.Parameters.AddWithValue("$id", idStr);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Delete child backtest runs
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM backtest_runs WHERE optimization_run_id = $id";
            cmd.Parameters.AddWithValue("$id", idStr);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Delete validation stage results (grandchild — must go before validation_runs)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM validation_stage_results WHERE validation_run_id IN (SELECT id FROM validation_runs WHERE optimization_run_id = $id)";
            cmd.Parameters.AddWithValue("$id", idStr);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Delete validation runs
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM validation_runs WHERE optimization_run_id = $id";
            cmd.Parameters.AddWithValue("$id", idStr);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Delete simulation cache metadata
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM simulation_cache_metadata WHERE optimization_run_id = $id";
            cmd.Parameters.AddWithValue("$id", idStr);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Delete parent optimization run
        int affected;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM optimization_runs WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", idStr);
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
        return affected > 0;
    }

    // ── Delete standalone backtest ─────────────────────────────────────

    public async Task<bool> DeleteBacktestAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM backtest_runs WHERE id = $id AND optimization_run_id IS NULL";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        return affected > 0;
    }

    // ── Distinct strategy names ────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetDistinctStrategyNamesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT strategy_name
            FROM (
                SELECT strategy_name, started_at FROM backtest_runs
                UNION ALL
                SELECT strategy_name, started_at FROM optimization_runs
            )
            GROUP BY strategy_name
            ORDER BY MAX(started_at) DESC
            """;

        var names = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));

        return names;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static BacktestRunRecord ReadBacktestRunCore(DbDataReader reader, bool includeEquityCurve)
    {
        var optIdStr = reader.IsDBNull(reader.GetOrdinal("optimization_run_id"))
            ? null
            : reader.GetString(reader.GetOrdinal("optimization_run_id"));

        return new BacktestRunRecord
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            StrategyName = reader.GetString(reader.GetOrdinal("strategy_name")),
            StrategyVersion = reader.GetString(reader.GetOrdinal("strategy_version")),
            Parameters = DeserializeParameters(reader.GetString(reader.GetOrdinal("parameters_json"))),
            DataSubscription = new DataSubscriptionDto
            {
                AssetName = reader.GetString(reader.GetOrdinal("asset_name")),
                Exchange = reader.GetString(reader.GetOrdinal("exchange")),
                TimeFrame = reader.GetString(reader.GetOrdinal("timeframe")),
            },
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = decimal.Parse(reader.GetString(reader.GetOrdinal("initial_cash")), CultureInfo.InvariantCulture),
                StartTime = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("data_start")), CultureInfo.InvariantCulture),
                EndTime = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("data_end")), CultureInfo.InvariantCulture),
                CommissionPerTrade = decimal.Parse(reader.GetString(reader.GetOrdinal("commission")), CultureInfo.InvariantCulture),
                SlippageTicks = reader.GetInt32(reader.GetOrdinal("slippage_ticks")),
            },
            StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at")), CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("completed_at")), CultureInfo.InvariantCulture),
            DurationMs = reader.GetInt64(reader.GetOrdinal("duration_ms")),
            TotalBars = reader.GetInt32(reader.GetOrdinal("total_bars")),
            Metrics = JsonSerializer.Deserialize<PerformanceMetrics>(
                reader.GetString(reader.GetOrdinal("metrics_json")), JsonOptions)!,
            EquityCurve = includeEquityCurve
                ? DeserializeEquityCurve(reader.GetString(reader.GetOrdinal("equity_curve_json")))
                : [],
            TradePnl = includeEquityCurve
                ? DeserializeTradePnl(reader.GetString(reader.GetOrdinal("trade_pnl_json")))
                : [],
            RunFolderPath = reader.IsDBNull(reader.GetOrdinal("run_folder_path"))
                ? null
                : reader.GetString(reader.GetOrdinal("run_folder_path")),
            RunMode = reader.GetString(reader.GetOrdinal("run_mode")),
            OptimizationRunId = optIdStr is not null ? Guid.Parse(optIdStr) : null,
            FitnessScore = reader.IsDBNull(reader.GetOrdinal("fitness_score"))
                ? null
                : reader.GetDouble(reader.GetOrdinal("fitness_score")),
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message"))
                ? null
                : reader.GetString(reader.GetOrdinal("error_message")),
            ErrorStackTrace = reader.IsDBNull(reader.GetOrdinal("error_stack_trace"))
                ? null
                : reader.GetString(reader.GetOrdinal("error_stack_trace")),
        };
    }

    private static OptimizationRunRecord ReadOptimizationRun(DbDataReader reader)
    {
        var completedAtRaw = reader.GetString(reader.GetOrdinal("completed_at"));
        var errorMessage = reader.IsDBNull(reader.GetOrdinal("error_message"))
            ? null
            : reader.GetString(reader.GetOrdinal("error_message"));
        var startedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at")), CultureInfo.InvariantCulture);
        var completedAt = completedAtRaw == ""
            ? startedAt
            : DateTimeOffset.Parse(completedAtRaw, CultureInfo.InvariantCulture);

        return new OptimizationRunRecord
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            StrategyName = reader.GetString(reader.GetOrdinal("strategy_name")),
            StrategyVersion = reader.GetString(reader.GetOrdinal("strategy_version")),
            StartedAt = startedAt,
            CompletedAt = completedAt,
            DurationMs = reader.GetInt64(reader.GetOrdinal("duration_ms")),
            TotalCombinations = reader.GetInt64(reader.GetOrdinal("total_combinations")),
            SortBy = reader.GetString(reader.GetOrdinal("sort_by")),
            DataSubscription = new DataSubscriptionDto
            {
                AssetName = reader.GetString(reader.GetOrdinal("asset_name")),
                Exchange = reader.GetString(reader.GetOrdinal("exchange")),
                TimeFrame = reader.GetString(reader.GetOrdinal("timeframe")),
            },
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = decimal.Parse(reader.GetString(reader.GetOrdinal("initial_cash")), CultureInfo.InvariantCulture),
                StartTime = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("data_start")), CultureInfo.InvariantCulture),
                EndTime = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("data_end")), CultureInfo.InvariantCulture),
                CommissionPerTrade = decimal.Parse(reader.GetString(reader.GetOrdinal("commission")), CultureInfo.InvariantCulture),
                SlippageTicks = reader.GetInt32(reader.GetOrdinal("slippage_ticks")),
            },
            MaxParallelism = reader.GetInt32(reader.GetOrdinal("max_parallelism")),
            FilteredTrials = reader.GetInt64(reader.GetOrdinal("filtered_trials")),
            FailedTrials = reader.GetInt64(reader.GetOrdinal("failed_trials")),
            InputJson = reader.IsDBNull(reader.GetOrdinal("input_json"))
                ? null
                : reader.GetString(reader.GetOrdinal("input_json")),
            ErrorMessage = errorMessage,
            OptimizationMethod = reader.IsDBNull(reader.GetOrdinal("optimization_method"))
                ? null
                : reader.GetString(reader.GetOrdinal("optimization_method")),
            GenerationsCompleted = reader.IsDBNull(reader.GetOrdinal("generations_completed"))
                ? null
                : reader.GetInt32(reader.GetOrdinal("generations_completed")),
            Status = reader.GetString(reader.GetOrdinal("status")),
            Trials = [], // loaded separately
        };
    }

    private static string GetTrialOrderByClause(string sortBy)
    {
        var cmp = StringComparison.OrdinalIgnoreCase;
        if (sortBy.Equals(MetricNames.Fitness, cmp))      return " ORDER BY fitness_score DESC NULLS LAST";
        if (sortBy.Equals(MetricNames.SharpeRatio, cmp))   return " ORDER BY json_extract(metrics_json, '$.sharpeRatio') DESC";
        if (sortBy.Equals(MetricNames.NetProfit, cmp))     return " ORDER BY json_extract(metrics_json, '$.netProfit') DESC";
        if (sortBy.Equals(MetricNames.SortinoRatio, cmp))  return " ORDER BY json_extract(metrics_json, '$.sortinoRatio') DESC";
        if (sortBy.Equals(MetricNames.ProfitFactor, cmp))  return " ORDER BY json_extract(metrics_json, '$.profitFactor') DESC";
        if (sortBy.Equals(MetricNames.WinRatePct, cmp))    return " ORDER BY json_extract(metrics_json, '$.winRatePct') DESC";
        if (sortBy.Equals(MetricNames.MaxDrawdownPct, cmp)) return " ORDER BY json_extract(metrics_json, '$.maxDrawdownPct') ASC";
        return " ORDER BY fitness_score DESC NULLS LAST";
    }

    private static string SerializeEquityCurve(IReadOnlyList<EquityPoint> curve)
    {
        // Compact format: [{t:timestampMs, v:equityValue}]
        var points = curve.Select(p => new { t = p.TimestampMs, v = p.Value });
        return JsonSerializer.Serialize(points);
    }

    private static IReadOnlyList<EquityPoint> DeserializeEquityCurve(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var points = new List<EquityPoint>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var t = element.GetProperty("t").GetInt64();
            var v = element.GetProperty("v").GetDouble();
            points.Add(new EquityPoint(t, v));
        }
        return points;
    }

    private static string SerializeTradePnl(IReadOnlyList<TradePoint> trades)
    {
        var points = trades.Select(t => new { t = t.TimestampMs, p = t.Pnl });
        return JsonSerializer.Serialize(points);
    }

    private static IReadOnlyList<TradePoint> DeserializeTradePnl(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var trades = new List<TradePoint>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var t = element.GetProperty("t").GetInt64();
            var p = element.GetProperty("p").GetDouble();
            trades.Add(new TradePoint(t, p));
        }
        return trades;
    }

    // ── Get trade PnL by ID ──────────────────────────────────────────

    public async Task<IReadOnlyList<TradePoint>?> GetTradePnlAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT trade_pnl_json FROM backtest_runs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is null or DBNull)
            return null;

        return DeserializeTradePnl((string)result);
    }

    private static IReadOnlyDictionary<string, object> DeserializeParameters(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var dict = new Dictionary<string, object>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.Number when prop.Value.TryGetInt64(out var l) => l,
                JsonValueKind.Number => prop.Value.GetDecimal(),
                JsonValueKind.String => prop.Value.GetString()!,
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => prop.Value.GetRawText()
            };
        }
        return dict;
    }
}
