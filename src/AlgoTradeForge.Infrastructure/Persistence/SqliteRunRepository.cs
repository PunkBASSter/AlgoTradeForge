using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
        error_message, error_stack_trace
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
                run_folder_path, run_mode, optimization_run_id,
                asset_name, exchange, timeframe,
                error_message, error_stack_trace
            ) VALUES (
                $id, $stratName, $stratVer, $paramsJson,
                $cash, $commission, $slippage,
                $startedAt, $completedAt, $dataStart, $dataEnd,
                $durationMs, $totalBars, $metricsJson, $equityJson,
                $runFolder, $runMode, $optId,
                $asset, $exchange, $tf,
                $errorMsg, $errorStack
            )
            """;

        cmd.Parameters.AddWithValue("$id", r.Id.ToString());
        cmd.Parameters.AddWithValue("$stratName", r.StrategyName);
        cmd.Parameters.AddWithValue("$stratVer", r.StrategyVersion);
        cmd.Parameters.AddWithValue("$paramsJson", JsonSerializer.Serialize(r.Parameters, JsonOptions));
        cmd.Parameters.AddWithValue("$cash", r.InitialCash.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$commission", r.Commission.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$slippage", r.SlippageTicks);
        cmd.Parameters.AddWithValue("$startedAt", r.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$completedAt", r.CompletedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$dataStart", r.DataStart.ToString("O"));
        cmd.Parameters.AddWithValue("$dataEnd", r.DataEnd.ToString("O"));
        cmd.Parameters.AddWithValue("$durationMs", r.DurationMs);
        cmd.Parameters.AddWithValue("$totalBars", r.TotalBars);
        cmd.Parameters.AddWithValue("$metricsJson", JsonSerializer.Serialize(r.Metrics, JsonOptions));
        cmd.Parameters.AddWithValue("$equityJson", SerializeEquityCurve(r.EquityCurve));
        cmd.Parameters.AddWithValue("$runFolder", (object?)r.RunFolderPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$runMode", r.RunMode);
        cmd.Parameters.AddWithValue("$optId", r.OptimizationRunId.HasValue ? r.OptimizationRunId.Value.ToString() : DBNull.Value);
        cmd.Parameters.AddWithValue("$asset", r.AssetName);
        cmd.Parameters.AddWithValue("$exchange", r.Exchange);
        cmd.Parameters.AddWithValue("$tf", r.TimeFrame);
        cmd.Parameters.AddWithValue("$errorMsg", (object?)r.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$errorStack", (object?)r.ErrorStackTrace ?? DBNull.Value);

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

    // ── Save optimization ──────────────────────────────────────────────

    public async Task SaveOptimizationAsync(OptimizationRunRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);
        using var tx = conn.BeginTransaction();

        // Insert parent optimization run
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO optimization_runs (
                    id, strategy_name, strategy_version,
                    started_at, completed_at, duration_ms, total_combinations,
                    sort_by, data_start, data_end,
                    initial_cash, commission, slippage_ticks, max_parallelism,
                    asset_name, exchange, timeframe, filtered_trials, failed_trials
                ) VALUES (
                    $id, $stratName, $stratVer,
                    $startedAt, $completedAt, $durationMs, $totalCombinations,
                    $sortBy, $dataStart, $dataEnd,
                    $cash, $commission, $slippage, $maxParallelism,
                    $asset, $exchange, $tf, $filteredTrials, $failedTrials
                )
                """;

            cmd.Parameters.AddWithValue("$id", record.Id.ToString());
            cmd.Parameters.AddWithValue("$stratName", record.StrategyName);
            cmd.Parameters.AddWithValue("$stratVer", record.StrategyVersion);
            cmd.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$completedAt", record.CompletedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$durationMs", record.DurationMs);
            cmd.Parameters.AddWithValue("$totalCombinations", record.TotalCombinations);
            cmd.Parameters.AddWithValue("$sortBy", record.SortBy);
            cmd.Parameters.AddWithValue("$dataStart", record.DataStart.ToString("O"));
            cmd.Parameters.AddWithValue("$dataEnd", record.DataEnd.ToString("O"));
            cmd.Parameters.AddWithValue("$cash", record.InitialCash.ToString(CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$commission", record.Commission.ToString(CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$slippage", record.SlippageTicks);
            cmd.Parameters.AddWithValue("$maxParallelism", record.MaxParallelism);
            cmd.Parameters.AddWithValue("$asset", record.AssetName);
            cmd.Parameters.AddWithValue("$exchange", record.Exchange);
            cmd.Parameters.AddWithValue("$tf", record.TimeFrame);
            cmd.Parameters.AddWithValue("$filteredTrials", record.FilteredTrials);
            cmd.Parameters.AddWithValue("$failedTrials", record.FailedTrials);

            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Insert child trial backtest runs
        foreach (var trial in record.Trials)
        {
            await InsertBacktestRunAsync(conn, tx, trial, ct);
        }

        tx.Commit();
    }

    // ── Get optimization by ID ─────────────────────────────────────────

    public async Task<OptimizationRunRecord?> GetOptimizationByIdAsync(Guid id, CancellationToken ct = default)
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
                trials.Add(ReadBacktestRunCore(trialReader, includeEquityCurve: false));
        }

        return record with { Trials = trials };
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
            conditions.Add("opr.completed_at >= $from");
            parameters.Add(new SqliteParameter("$from", query.From.Value.ToString("O")));
        }
        if (query.To is not null)
        {
            conditions.Add("opr.completed_at <= $to");
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

        // Fetch page
        var sb = new StringBuilder("SELECT * FROM optimization_runs opr");
        sb.Append(whereClause);
        sb.Append(" ORDER BY opr.completed_at DESC");
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

    // ── Distinct strategy names ────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetDistinctStrategyNamesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT strategy_name FROM backtest_runs
            UNION
            SELECT DISTINCT strategy_name FROM optimization_runs
            ORDER BY strategy_name
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
            AssetName = reader.GetString(reader.GetOrdinal("asset_name")),
            Exchange = reader.GetString(reader.GetOrdinal("exchange")),
            TimeFrame = reader.GetString(reader.GetOrdinal("timeframe")),
            InitialCash = decimal.Parse(reader.GetString(reader.GetOrdinal("initial_cash")), CultureInfo.InvariantCulture),
            Commission = decimal.Parse(reader.GetString(reader.GetOrdinal("commission")), CultureInfo.InvariantCulture),
            SlippageTicks = reader.GetInt32(reader.GetOrdinal("slippage_ticks")),
            StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at")), CultureInfo.InvariantCulture),
            CompletedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("completed_at")), CultureInfo.InvariantCulture),
            DataStart = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("data_start")), CultureInfo.InvariantCulture),
            DataEnd = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("data_end")), CultureInfo.InvariantCulture),
            DurationMs = reader.GetInt64(reader.GetOrdinal("duration_ms")),
            TotalBars = reader.GetInt32(reader.GetOrdinal("total_bars")),
            Metrics = JsonSerializer.Deserialize<PerformanceMetrics>(
                reader.GetString(reader.GetOrdinal("metrics_json")), JsonOptions)!,
            EquityCurve = includeEquityCurve
                ? DeserializeEquityCurve(reader.GetString(reader.GetOrdinal("equity_curve_json")))
                : [],
            RunFolderPath = reader.IsDBNull(reader.GetOrdinal("run_folder_path"))
                ? null
                : reader.GetString(reader.GetOrdinal("run_folder_path")),
            RunMode = reader.GetString(reader.GetOrdinal("run_mode")),
            OptimizationRunId = optIdStr is not null ? Guid.Parse(optIdStr) : null,
            ErrorMessage = reader.IsDBNull(reader.GetOrdinal("error_message"))
                ? null
                : reader.GetString(reader.GetOrdinal("error_message")),
            ErrorStackTrace = reader.IsDBNull(reader.GetOrdinal("error_stack_trace"))
                ? null
                : reader.GetString(reader.GetOrdinal("error_stack_trace")),
        };
    }

    private static OptimizationRunRecord ReadOptimizationRun(DbDataReader reader) => new()
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
        StrategyName = reader.GetString(reader.GetOrdinal("strategy_name")),
        StrategyVersion = reader.GetString(reader.GetOrdinal("strategy_version")),
        StartedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("started_at")), CultureInfo.InvariantCulture),
        CompletedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("completed_at")), CultureInfo.InvariantCulture),
        DurationMs = reader.GetInt64(reader.GetOrdinal("duration_ms")),
        TotalCombinations = reader.GetInt64(reader.GetOrdinal("total_combinations")),
        SortBy = reader.GetString(reader.GetOrdinal("sort_by")),
        DataStart = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("data_start")), CultureInfo.InvariantCulture),
        DataEnd = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("data_end")), CultureInfo.InvariantCulture),
        InitialCash = decimal.Parse(reader.GetString(reader.GetOrdinal("initial_cash")), CultureInfo.InvariantCulture),
        Commission = decimal.Parse(reader.GetString(reader.GetOrdinal("commission")), CultureInfo.InvariantCulture),
        SlippageTicks = reader.GetInt32(reader.GetOrdinal("slippage_ticks")),
        MaxParallelism = reader.GetInt32(reader.GetOrdinal("max_parallelism")),
        AssetName = reader.GetString(reader.GetOrdinal("asset_name")),
        Exchange = reader.GetString(reader.GetOrdinal("exchange")),
        TimeFrame = reader.GetString(reader.GetOrdinal("timeframe")),
        FilteredTrials = reader.GetInt64(reader.GetOrdinal("filtered_trials")),
        FailedTrials = reader.GetInt64(reader.GetOrdinal("failed_trials")),
        Trials = [], // loaded separately
    };

    private static string GetTrialOrderByClause(string sortBy) => sortBy switch
    {
        "SharpeRatio"    => " ORDER BY json_extract(metrics_json, '$.sharpeRatio') DESC",
        "NetProfit"      => " ORDER BY json_extract(metrics_json, '$.netProfit') DESC",
        "SortinoRatio"   => " ORDER BY json_extract(metrics_json, '$.sortinoRatio') DESC",
        "ProfitFactor"   => " ORDER BY json_extract(metrics_json, '$.profitFactor') DESC",
        "WinRatePct"     => " ORDER BY json_extract(metrics_json, '$.winRatePct') DESC",
        "MaxDrawdownPct" => " ORDER BY json_extract(metrics_json, '$.maxDrawdownPct') ASC",
        _                => " ORDER BY json_extract(metrics_json, '$.sharpeRatio') DESC",
    };

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
            var v = element.GetProperty("v").GetDecimal();
            points.Add(new EquityPoint(t, v));
        }
        return points;
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
