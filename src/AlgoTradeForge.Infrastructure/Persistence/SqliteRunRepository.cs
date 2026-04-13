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
        error_message, error_stack_trace, fitness_score,
        subscriptions_json
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
                error_message, error_stack_trace, fitness_score,
                subscriptions_json,
                sharpe_ratio, sortino_ratio, profit_factor, max_drawdown_pct,
                win_rate_pct, total_trades, net_profit, annualized_return_pct
            ) VALUES (
                $id, $stratName, $stratVer, $paramsJson,
                $cash, $commission, $slippage,
                $startedAt, $completedAt, $dataStart, $dataEnd,
                $durationMs, $totalBars, $metricsJson, $equityJson,
                $tradePnlJson,
                $runFolder, $runMode, $optId,
                $asset, $exchange, $tf,
                $errorMsg, $errorStack, $fitnessScore,
                $subscriptionsJson,
                $sharpe, $sortino, $pf, $maxDd,
                $winRate, $trades, $netProfit, $annRet
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
        cmd.Parameters.AddWithValue("$asset", r.DataSubscriptions[0].AssetName);
        cmd.Parameters.AddWithValue("$exchange", r.DataSubscriptions[0].Exchange);
        cmd.Parameters.AddWithValue("$tf", r.DataSubscriptions[0].TimeFrame);
        cmd.Parameters.AddWithValue("$errorMsg", (object?)r.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errorStack", (object?)r.ErrorStackTrace ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fitnessScore",
            r.FitnessScore is { } fs && double.IsFinite(fs) ? fs : DBNull.Value);
        cmd.Parameters.AddWithValue("$subscriptionsJson",
            JsonSerializer.Serialize(r.DataSubscriptions, JsonOptions));

        var m = r.Metrics;
        cmd.Parameters.AddWithValue("$sharpe", double.IsFinite(m.SharpeRatio) ? m.SharpeRatio : DBNull.Value);
        cmd.Parameters.AddWithValue("$sortino", double.IsFinite(m.SortinoRatio) ? m.SortinoRatio : DBNull.Value);
        cmd.Parameters.AddWithValue("$pf", double.IsFinite(m.ProfitFactor) ? m.ProfitFactor : DBNull.Value);
        cmd.Parameters.AddWithValue("$maxDd", double.IsFinite(m.MaxDrawdownPct) ? m.MaxDrawdownPct : DBNull.Value);
        cmd.Parameters.AddWithValue("$winRate", double.IsFinite(m.WinRatePct) ? m.WinRatePct : DBNull.Value);
        cmd.Parameters.AddWithValue("$trades", m.TotalTrades);
        cmd.Parameters.AddWithValue("$netProfit", double.IsFinite((double)m.NetProfit) ? (double)m.NetProfit : DBNull.Value);
        cmd.Parameters.AddWithValue("$annRet", double.IsFinite(m.AnnualizedReturnPct) ? m.AnnualizedReturnPct : DBNull.Value);

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
        var orderClause = query.SortBy is not null
            ? GetTrialOrderByClause(query.SortBy, "br")
            : " ORDER BY br.completed_at DESC";
        sb.Append(orderClause);
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
                optimization_method, generations_completed, input_json, error_message, status,
                subscriptions_json, group_id, dss_index
            ) VALUES (
                $id, $stratName, $stratVer,
                $startedAt, '', 0, $totalCombinations,
                $sortBy, $dataStart, $dataEnd,
                $cash, $commission, $slippage, $maxParallelism,
                $asset, $exchange, $tf, 0, 0,
                $optMethod, NULL, $inputJson, NULL, $status,
                $subscriptionsJson, $groupId, $dssIndex
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
        cmd.Parameters.AddWithValue("$asset", record.DataSubscriptions[0].AssetName);
        cmd.Parameters.AddWithValue("$exchange", record.DataSubscriptions[0].Exchange);
        cmd.Parameters.AddWithValue("$tf", record.DataSubscriptions[0].TimeFrame);
        cmd.Parameters.AddWithValue("$optMethod", (object?)record.OptimizationMethod ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$inputJson", (object?)record.InputJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$status", record.Status);
        cmd.Parameters.AddWithValue("$subscriptionsJson",
            JsonSerializer.Serialize(record.DataSubscriptions, JsonOptions));
        cmd.Parameters.AddWithValue("$groupId",
            record.GroupId.HasValue ? record.GroupId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$dssIndex", record.DssIndex);

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
                    dedup_skipped = $dedupSkipped,
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
            cmd.Parameters.AddWithValue("$dedupSkipped", record.DedupSkipped);
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

        var trials = new List<BacktestRunRecord>();
        int trialCount;

        if (includeEquityCurves)
        {
            // Validation path: load all trials with equity curves
            await using var trialCmd = conn.CreateCommand();
            var orderClause = GetTrialOrderByClause(record.SortBy);
            trialCmd.CommandText = $"SELECT * FROM backtest_runs WHERE optimization_run_id = $optId{orderClause}";
            trialCmd.Parameters.AddWithValue("$optId", id.ToString());

            await using var trialReader = await trialCmd.ExecuteReaderAsync(ct);
            while (await trialReader.ReadAsync(ct))
                trials.Add(ReadBacktestRunCore(trialReader, includeEquityCurve: true));

            trialCount = trials.Count;
        }
        else
        {
            // Detail view path: count only, trials loaded via GetOptimizationTrialsAsync
            await using var countCmd = conn.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM backtest_runs WHERE optimization_run_id = $optId";
            countCmd.Parameters.AddWithValue("$optId", id.ToString());
            trialCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));
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

        return record with { TrialCount = trialCount, Trials = trials, FailedTrialDetails = failedDetails };
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

    // ── Paginated optimization trials ────────────────────────────────────

    public async Task<PagedResult<BacktestRunRecord>> GetOptimizationTrialsAsync(
        Guid optimizationId, int limit = 50, int offset = 0,
        string? sortBy = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        var idStr = optimizationId.ToString();

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM backtest_runs WHERE optimization_run_id = $optId";
        countCmd.Parameters.AddWithValue("$optId", idStr);
        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        var orderClause = GetTrialOrderByClause(sortBy ?? MetricNames.Fitness);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {BacktestListColumns} FROM backtest_runs WHERE optimization_run_id = $optId{orderClause} LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$optId", idStr);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        cmd.Parameters.AddWithValue("$offset", Math.Max(offset, 0));

        var results = new List<BacktestRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadBacktestRunCore(reader, includeEquityCurve: false));

        return new PagedResult<BacktestRunRecord>(results, totalCount);
    }

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
        sb.Append(" ORDER BY CASE WHEN opr.status IN ('InProgress', 'Enqueued') THEN 0 ELSE 1 END, opr.started_at DESC");
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

        var subscriptionsOrd = TryGetOrdinal(reader, "subscriptions_json");
        var subscriptionsJson = subscriptionsOrd is int sOrd && !reader.IsDBNull(sOrd)
            ? reader.GetString(sOrd)
            : null;

        var dataSubscriptions = subscriptionsJson is not null
            ? (IReadOnlyList<DataSubscriptionDto>)JsonSerializer.Deserialize<List<DataSubscriptionDto>>(subscriptionsJson, JsonOptions)!
            : [new DataSubscriptionDto
            {
                AssetName = reader.GetString(reader.GetOrdinal("asset_name")),
                Exchange = reader.GetString(reader.GetOrdinal("exchange")),
                TimeFrame = reader.GetString(reader.GetOrdinal("timeframe")),
            }];

        var parameters = DeserializeParameters(reader.GetString(reader.GetOrdinal("parameters_json")));

        return new BacktestRunRecord
        {
            Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
            StrategyName = reader.GetString(reader.GetOrdinal("strategy_name")),
            StrategyVersion = reader.GetString(reader.GetOrdinal("strategy_version")),
            Parameters = parameters,
            DataSubscriptions = dataSubscriptions,
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
            // Build from already-deserialized dict (avoids re-parsing parameters_json)
            Params = BuildParamsCsv(parameters),
        };
    }

    private static string BuildParamsCsv(IReadOnlyDictionary<string, object> parameters)
    {
        var parts = new List<string>();
        foreach (var (key, value) in parameters)
        {
            if (key is "DataSubscriptions")
                continue;
            var val = value switch
            {
                JsonElement je when je.ValueKind == JsonValueKind.Object =>
                    je.TryGetProperty("TypeKey", out var tk) ? tk.GetString() ?? je.GetRawText() : je.GetRawText(),
                JsonElement je => je.ToString(),
                _ => string.Format(CultureInfo.InvariantCulture, "{0}", value),
            };
            parts.Add($"{key}:{val}");
        }
        return string.Join(", ", parts);
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

        var subscriptionsOrd = TryGetOrdinal(reader, "subscriptions_json");
        var subscriptionsJson = subscriptionsOrd is int sOrd && !reader.IsDBNull(sOrd)
            ? reader.GetString(sOrd)
            : null;

        var dataSubscriptions = subscriptionsJson is not null
            ? (IReadOnlyList<DataSubscriptionDto>)JsonSerializer.Deserialize<List<DataSubscriptionDto>>(subscriptionsJson, JsonOptions)!
            : [new DataSubscriptionDto
            {
                AssetName = reader.GetString(reader.GetOrdinal("asset_name")),
                Exchange = reader.GetString(reader.GetOrdinal("exchange")),
                TimeFrame = reader.GetString(reader.GetOrdinal("timeframe")),
            }];

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
            DataSubscriptions = dataSubscriptions,
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
            DedupSkipped = TryGetOrdinal(reader, "dedup_skipped") is int dedupOrd && !reader.IsDBNull(dedupOrd)
                ? reader.GetInt64(dedupOrd)
                : 0,
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
            GroupId = TryGetOrdinal(reader, "group_id") is int gOrd && !reader.IsDBNull(gOrd)
                ? Guid.Parse(reader.GetString(gOrd))
                : null,
            DssIndex = TryGetOrdinal(reader, "dss_index") is int dOrd && !reader.IsDBNull(dOrd)
                ? reader.GetInt32(dOrd)
                : 0,
            Trials = [], // loaded separately
        };
    }

    private static string GetTrialOrderByClause(string sortBy, string tableAlias = "")
    {
        var prefix = string.IsNullOrEmpty(tableAlias) ? "" : tableAlias + ".";
        var cmp = StringComparison.OrdinalIgnoreCase;
        if (sortBy.Equals(MetricNames.Fitness, cmp))       return $" ORDER BY {prefix}fitness_score DESC NULLS LAST";
        if (sortBy.Equals(MetricNames.SharpeRatio, cmp))   return $" ORDER BY {prefix}sharpe_ratio DESC NULLS LAST";
        if (sortBy.Equals(MetricNames.NetProfit, cmp))     return $" ORDER BY {prefix}net_profit DESC NULLS LAST";
        if (sortBy.Equals(MetricNames.SortinoRatio, cmp))  return $" ORDER BY {prefix}sortino_ratio DESC NULLS LAST";
        if (sortBy.Equals(MetricNames.ProfitFactor, cmp))  return $" ORDER BY {prefix}profit_factor DESC NULLS LAST";
        if (sortBy.Equals(MetricNames.WinRatePct, cmp))    return $" ORDER BY {prefix}win_rate_pct DESC NULLS LAST";
        if (sortBy.Equals(MetricNames.MaxDrawdownPct, cmp)) return $" ORDER BY {prefix}max_drawdown_pct ASC NULLS LAST";
        if (sortBy.Equals("TotalTrades", cmp))             return $" ORDER BY {prefix}total_trades DESC NULLS LAST";
        if (sortBy.Equals("AnnualizedReturnPct", cmp))     return $" ORDER BY {prefix}annualized_return_pct DESC NULLS LAST";
        return $" ORDER BY {prefix}fitness_score DESC NULLS LAST";
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

    /// <summary>
    /// Safely gets column ordinal, returning null if the column does not exist.
    /// Handles backward-compatible reads from databases that predate the column.
    /// </summary>
    private static int? TryGetOrdinal(System.Data.Common.DbDataReader reader, string name)
    {
        try { return reader.GetOrdinal(name); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ── Optimization group operations ──────────────────────────────────

    public async Task InsertOptimizationGroupAsync(OptimizationGroupRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO optimization_groups (
                id, strategy_name, strategy_version, optimization_method,
                started_at, completed_at, total_runs, status,
                input_json, subscriptions_json, backtest_settings_json,
                optimization_settings_json, fitness_config_json, max_parallelism
            ) VALUES (
                $id, $stratName, $stratVer, $optMethod,
                $startedAt, NULL, $totalRuns, 'InProgress',
                $inputJson, $subsJson, $bsJson,
                $osJson, $fcJson, $maxPar
            )
            """;

        cmd.Parameters.AddWithValue("$id", record.Id.ToString());
        cmd.Parameters.AddWithValue("$stratName", record.StrategyName);
        cmd.Parameters.AddWithValue("$stratVer", (object?)record.StrategyVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$optMethod", record.OptimizationMethod);
        cmd.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$totalRuns", record.TotalRuns);
        cmd.Parameters.AddWithValue("$inputJson", (object?)record.InputJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$subsJson", record.SubscriptionsJson);
        cmd.Parameters.AddWithValue("$bsJson", record.BacktestSettingsJson);
        cmd.Parameters.AddWithValue("$osJson", (object?)record.OptimizationSettingsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fcJson", (object?)record.FitnessConfigJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxPar", record.MaxParallelism);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<OptimizationGroupRecord?> GetOptimizationGroupByIdAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        OptimizationGroupRecord? group;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM optimization_groups WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;

            group = ReadOptimizationGroup(reader);
        }

        // Load child runs
        await using (var runsCmd = conn.CreateCommand())
        {
            runsCmd.CommandText = "SELECT * FROM optimization_runs WHERE group_id = $groupId ORDER BY dss_index";
            runsCmd.Parameters.AddWithValue("$groupId", id.ToString());

            var runs = new List<OptimizationRunRecord>();
            await using var runsReader = await runsCmd.ExecuteReaderAsync(ct);
            while (await runsReader.ReadAsync(ct))
                runs.Add(ReadOptimizationRun(runsReader));

            group = group with { Runs = runs };
        }

        return group;
    }

    public async Task<PagedResult<OptimizationGroupRecord>> QueryOptimizationGroupsAsync(
        OptimizationGroupQuery query, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        var parameters = new List<SqliteParameter>();
        var conditions = new List<string>();

        if (query.StrategyName is not null)
        {
            conditions.Add("og.strategy_name = $stratName");
            parameters.Add(new SqliteParameter("$stratName", query.StrategyName));
        }
        if (query.From is not null)
        {
            conditions.Add("og.started_at >= $from");
            parameters.Add(new SqliteParameter("$from", query.From.Value.ToString("O")));
        }
        if (query.To is not null)
        {
            conditions.Add("og.started_at <= $to");
            parameters.Add(new SqliteParameter("$to", query.To.Value.ToString("O")));
        }

        var whereClause = conditions.Count > 0
            ? " WHERE " + string.Join(" AND ", conditions)
            : "";

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = $"SELECT COUNT(*) FROM optimization_groups og{whereClause}";
        foreach (var p in parameters)
            countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        var sb = new StringBuilder("SELECT * FROM optimization_groups og");
        sb.Append(whereClause);
        sb.Append(" ORDER BY CASE WHEN og.status = 'InProgress' THEN 0 ELSE 1 END, og.started_at DESC");
        sb.Append(" LIMIT $limit OFFSET $offset");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        foreach (var p in parameters)
            cmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        cmd.Parameters.Add(new SqliteParameter("$limit", query.Limit));
        cmd.Parameters.Add(new SqliteParameter("$offset", query.Offset));

        var results = new List<OptimizationGroupRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadOptimizationGroup(reader));

        return new PagedResult<OptimizationGroupRecord>(results, totalCount);
    }

    public async Task UpdateOptimizationRunStatusAsync(
        Guid runId, string status, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE optimization_runs
            SET status = $status
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", runId.ToString());
        cmd.Parameters.AddWithValue("$status", status);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateOptimizationGroupStatusAsync(
        Guid groupId, string status, DateTimeOffset? completedAt, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE optimization_groups
            SET status = $status, completed_at = $completedAt
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", groupId.ToString());
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$completedAt", completedAt.HasValue ? completedAt.Value.ToString("O") : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteOptimizationGroupAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);
        using var tx = conn.BeginTransaction();

        var idStr = id.ToString();

        // Get all child optimization run IDs
        var runIds = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM optimization_runs WHERE group_id = $gid";
            cmd.Parameters.AddWithValue("$gid", idStr);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                runIds.Add(reader.GetString(0));
        }

        foreach (var runId in runIds)
        {
            // Delete failed trials
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM optimization_failed_trials WHERE optimization_run_id = $rid";
                cmd.Parameters.AddWithValue("$rid", runId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Delete backtest trials
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM backtest_runs WHERE optimization_run_id = $rid";
                cmd.Parameters.AddWithValue("$rid", runId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Delete validation stage results
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM validation_stage_results WHERE validation_run_id IN (SELECT id FROM validation_runs WHERE optimization_run_id = $rid)";
                cmd.Parameters.AddWithValue("$rid", runId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Delete validation runs
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM validation_runs WHERE optimization_run_id = $rid";
                cmd.Parameters.AddWithValue("$rid", runId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Delete simulation cache metadata
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM simulation_cache_metadata WHERE optimization_run_id = $rid";
                cmd.Parameters.AddWithValue("$rid", runId);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // Delete child optimization runs
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM optimization_runs WHERE group_id = $gid";
            cmd.Parameters.AddWithValue("$gid", idStr);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Delete validation groups referencing this optimization group
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM validation_groups WHERE optimization_group_id = $gid";
            cmd.Parameters.AddWithValue("$gid", idStr);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Delete the optimization group itself
        int affected;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM optimization_groups WHERE id = $gid";
            cmd.Parameters.AddWithValue("$gid", idStr);
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
        return affected > 0;
    }

    public async Task<PagedResult<BacktestRunRecord>> GetOptimizationGroupTrialsAsync(
        Guid groupId, int limit = 1000, int offset = 0,
        string? sortBy = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        var gidStr = groupId.ToString();

        await using var countCmd = conn.CreateCommand();
        countCmd.CommandText = """
            SELECT COUNT(*) FROM backtest_runs br
            JOIN optimization_runs opr ON br.optimization_run_id = opr.id
            WHERE opr.group_id = $gid
            """;
        countCmd.Parameters.AddWithValue("$gid", gidStr);
        var totalCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        var qualifiedOrder = GetTrialOrderByClause(sortBy ?? MetricNames.Fitness, "br");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT br.* FROM backtest_runs br
            JOIN optimization_runs opr ON br.optimization_run_id = opr.id
            WHERE opr.group_id = $gid
            {qualifiedOrder} LIMIT $limit OFFSET $offset
            """;
        cmd.Parameters.AddWithValue("$gid", gidStr);
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        cmd.Parameters.AddWithValue("$offset", Math.Max(offset, 0));

        var results = new List<BacktestRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadBacktestRunCore(reader, includeEquityCurve: false));

        return new PagedResult<BacktestRunRecord>(results, totalCount);
    }

    private static OptimizationGroupRecord ReadOptimizationGroup(System.Data.Common.DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        StrategyName = reader.GetString(reader.GetOrdinal("strategy_name")),
        StrategyVersion = reader.IsDBNull(reader.GetOrdinal("strategy_version"))
            ? null : reader.GetString(reader.GetOrdinal("strategy_version")),
        OptimizationMethod = reader.GetString(reader.GetOrdinal("optimization_method")),
        StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at")), CultureInfo.InvariantCulture),
        CompletedAt = reader.IsDBNull(reader.GetOrdinal("completed_at"))
            ? null : DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("completed_at")), CultureInfo.InvariantCulture),
        TotalRuns = reader.GetInt32(reader.GetOrdinal("total_runs")),
        Status = reader.GetString(reader.GetOrdinal("status")),
        InputJson = reader.IsDBNull(reader.GetOrdinal("input_json"))
            ? null : reader.GetString(reader.GetOrdinal("input_json")),
        SubscriptionsJson = reader.GetString(reader.GetOrdinal("subscriptions_json")),
        BacktestSettingsJson = reader.GetString(reader.GetOrdinal("backtest_settings_json")),
        OptimizationSettingsJson = reader.IsDBNull(reader.GetOrdinal("optimization_settings_json"))
            ? null : reader.GetString(reader.GetOrdinal("optimization_settings_json")),
        FitnessConfigJson = reader.IsDBNull(reader.GetOrdinal("fitness_config_json"))
            ? null : reader.GetString(reader.GetOrdinal("fitness_config_json")),
        MaxParallelism = reader.GetInt32(reader.GetOrdinal("max_parallelism")),
    };
}
