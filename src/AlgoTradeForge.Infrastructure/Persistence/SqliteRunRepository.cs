using System.Globalization;
using System.Text;
using System.Text.Json;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Reporting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Persistence;

public sealed class SqliteRunRepository : IRunRepository
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SqliteRunRepository(IOptions<RunStorageOptions> options)
    {
        var dbPath = options.Value.DatabasePath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = $"Data Source={dbPath}";
    }

    private async Task EnsureInitializedAsync()
    {
        if (Volatile.Read(ref _initialized))
            return;

        await _initLock.WaitAsync();
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

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // ── Save backtest ──────────────────────────────────────────────────

    public async Task SaveAsync(BacktestRunRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();

        InsertBacktestRun(conn, tx, record);
        InsertBacktestDataSubscriptions(conn, tx, record.Id, record.DataSubscriptions);

        tx.Commit();
    }

    private static void InsertBacktestRun(SqliteConnection conn, SqliteTransaction tx, BacktestRunRecord r)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO backtest_runs (
                id, strategy_name, strategy_version, parameters_json,
                initial_cash, commission, slippage_ticks,
                started_at, completed_at, data_start, data_end,
                duration_ms, total_bars, metrics_json, equity_curve_json,
                run_folder_path, run_mode, optimization_run_id
            ) VALUES (
                $id, $stratName, $stratVer, $paramsJson,
                $cash, $commission, $slippage,
                $startedAt, $completedAt, $dataStart, $dataEnd,
                $durationMs, $totalBars, $metricsJson, $equityJson,
                $runFolder, $runMode, $optId
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

        cmd.ExecuteNonQuery();
    }

    private static void InsertBacktestDataSubscriptions(
        SqliteConnection conn, SqliteTransaction tx, Guid backtestRunId, IReadOnlyList<DataSubscriptionRecord> subs)
    {
        foreach (var sub in subs)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO backtest_data_subscriptions (backtest_run_id, asset_name, exchange, timeframe)
                VALUES ($runId, $asset, $exchange, $tf)
                """;
            cmd.Parameters.AddWithValue("$runId", backtestRunId.ToString());
            cmd.Parameters.AddWithValue("$asset", sub.AssetName);
            cmd.Parameters.AddWithValue("$exchange", sub.Exchange);
            cmd.Parameters.AddWithValue("$tf", sub.TimeFrame);
            cmd.ExecuteNonQuery();
        }
    }

    // ── Get backtest by ID ─────────────────────────────────────────────

    public async Task<BacktestRunRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        using var conn = CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM backtest_runs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var record = ReadBacktestRun(reader);
        var subs = LoadBacktestSubscriptions(conn, id);
        return record with { DataSubscriptions = subs };
    }

    // ── Query backtests ────────────────────────────────────────────────

    public async Task<IReadOnlyList<BacktestRunRecord>> QueryAsync(BacktestRunQuery query, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        using var conn = CreateConnection();

        var sb = new StringBuilder("SELECT DISTINCT br.* FROM backtest_runs br");
        var parameters = new List<SqliteParameter>();
        var needsJoin = query.AssetName is not null || query.Exchange is not null || query.TimeFrame is not null;

        if (needsJoin)
            sb.Append(" INNER JOIN backtest_data_subscriptions bds ON bds.backtest_run_id = br.id");

        var conditions = new List<string>();

        if (query.StrategyName is not null)
        {
            conditions.Add("br.strategy_name = $stratName");
            parameters.Add(new SqliteParameter("$stratName", query.StrategyName));
        }
        if (query.AssetName is not null)
        {
            conditions.Add("bds.asset_name = $asset");
            parameters.Add(new SqliteParameter("$asset", query.AssetName));
        }
        if (query.Exchange is not null)
        {
            conditions.Add("bds.exchange = $exchange");
            parameters.Add(new SqliteParameter("$exchange", query.Exchange));
        }
        if (query.TimeFrame is not null)
        {
            conditions.Add("bds.timeframe = $tf");
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

        if (conditions.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", conditions));

        sb.Append(" ORDER BY br.completed_at DESC");
        sb.Append(" LIMIT $limit OFFSET $offset");
        parameters.Add(new SqliteParameter("$limit", query.Limit));
        parameters.Add(new SqliteParameter("$offset", query.Offset));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        cmd.Parameters.AddRange(parameters);

        var results = new List<BacktestRunRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadBacktestRun(reader));

        // Load subscriptions for each result
        for (var i = 0; i < results.Count; i++)
        {
            var subs = LoadBacktestSubscriptions(conn, results[i].Id);
            results[i] = results[i] with { DataSubscriptions = subs };
        }

        return results;
    }

    // ── Save optimization ──────────────────────────────────────────────

    public async Task SaveOptimizationAsync(OptimizationRunRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        using var conn = CreateConnection();
        using var tx = conn.BeginTransaction();

        // Insert parent optimization run
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO optimization_runs (
                    id, strategy_name, strategy_version,
                    started_at, completed_at, duration_ms, total_combinations,
                    sort_by, data_start, data_end,
                    initial_cash, commission, slippage_ticks, max_parallelism
                ) VALUES (
                    $id, $stratName, $stratVer,
                    $startedAt, $completedAt, $durationMs, $totalCombinations,
                    $sortBy, $dataStart, $dataEnd,
                    $cash, $commission, $slippage, $maxParallelism
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

            cmd.ExecuteNonQuery();
        }

        // Insert optimization data subscriptions
        foreach (var sub in record.DataSubscriptions)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO optimization_data_subscriptions (optimization_run_id, asset_name, exchange, timeframe)
                VALUES ($optId, $asset, $exchange, $tf)
                """;
            cmd.Parameters.AddWithValue("$optId", record.Id.ToString());
            cmd.Parameters.AddWithValue("$asset", sub.AssetName);
            cmd.Parameters.AddWithValue("$exchange", sub.Exchange);
            cmd.Parameters.AddWithValue("$tf", sub.TimeFrame);
            cmd.ExecuteNonQuery();
        }

        // Insert child trial backtest runs
        foreach (var trial in record.Trials)
        {
            InsertBacktestRun(conn, tx, trial);
            InsertBacktestDataSubscriptions(conn, tx, trial.Id, trial.DataSubscriptions);
        }

        tx.Commit();
    }

    // ── Get optimization by ID ─────────────────────────────────────────

    public async Task<OptimizationRunRecord?> GetOptimizationByIdAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        using var conn = CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM optimization_runs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var record = ReadOptimizationRun(reader);

        // Load optimization data subscriptions
        var optSubs = LoadOptimizationSubscriptions(conn, id);

        // Load child trials
        var trials = new List<BacktestRunRecord>();
        using (var trialCmd = conn.CreateCommand())
        {
            trialCmd.CommandText = "SELECT * FROM backtest_runs WHERE optimization_run_id = $optId";
            trialCmd.Parameters.AddWithValue("$optId", id.ToString());

            using var trialReader = trialCmd.ExecuteReader();
            while (trialReader.Read())
                trials.Add(ReadBacktestRun(trialReader));
        }

        // Load subscriptions for each trial
        for (var i = 0; i < trials.Count; i++)
        {
            var subs = LoadBacktestSubscriptions(conn, trials[i].Id);
            trials[i] = trials[i] with { DataSubscriptions = subs };
        }

        return record with { DataSubscriptions = optSubs, Trials = trials };
    }

    // ── Query optimizations ────────────────────────────────────────────

    public async Task<IReadOnlyList<OptimizationRunRecord>> QueryOptimizationsAsync(
        OptimizationRunQuery query, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        using var conn = CreateConnection();

        var sb = new StringBuilder("SELECT DISTINCT opr.* FROM optimization_runs opr");
        var parameters = new List<SqliteParameter>();
        var needsJoin = query.AssetName is not null || query.Exchange is not null || query.TimeFrame is not null;

        if (needsJoin)
            sb.Append(" INNER JOIN optimization_data_subscriptions ods ON ods.optimization_run_id = opr.id");

        var conditions = new List<string>();

        if (query.StrategyName is not null)
        {
            conditions.Add("opr.strategy_name = $stratName");
            parameters.Add(new SqliteParameter("$stratName", query.StrategyName));
        }
        if (query.AssetName is not null)
        {
            conditions.Add("ods.asset_name = $asset");
            parameters.Add(new SqliteParameter("$asset", query.AssetName));
        }
        if (query.Exchange is not null)
        {
            conditions.Add("ods.exchange = $exchange");
            parameters.Add(new SqliteParameter("$exchange", query.Exchange));
        }
        if (query.TimeFrame is not null)
        {
            conditions.Add("ods.timeframe = $tf");
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

        if (conditions.Count > 0)
            sb.Append(" WHERE ").Append(string.Join(" AND ", conditions));

        sb.Append(" ORDER BY opr.completed_at DESC");
        sb.Append(" LIMIT $limit OFFSET $offset");
        parameters.Add(new SqliteParameter("$limit", query.Limit));
        parameters.Add(new SqliteParameter("$offset", query.Offset));

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        cmd.Parameters.AddRange(parameters);

        var results = new List<OptimizationRunRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadOptimizationRun(reader));

        // Load subscriptions and trials for each result
        for (var i = 0; i < results.Count; i++)
        {
            var optSubs = LoadOptimizationSubscriptions(conn, results[i].Id);
            results[i] = results[i] with { DataSubscriptions = optSubs, Trials = [] };
        }

        return results;
    }

    // ── Distinct strategy names ────────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetDistinctStrategyNamesAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        using var conn = CreateConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT strategy_name FROM backtest_runs
            UNION
            SELECT DISTINCT strategy_name FROM optimization_runs
            ORDER BY strategy_name
            """;

        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));

        return names;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static BacktestRunRecord ReadBacktestRun(SqliteDataReader reader)
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
            DataSubscriptions = [], // loaded separately
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
            EquityCurve = DeserializeEquityCurve(reader.GetString(reader.GetOrdinal("equity_curve_json"))),
            RunFolderPath = reader.IsDBNull(reader.GetOrdinal("run_folder_path"))
                ? null
                : reader.GetString(reader.GetOrdinal("run_folder_path")),
            RunMode = reader.GetString(reader.GetOrdinal("run_mode")),
            OptimizationRunId = optIdStr is not null ? Guid.Parse(optIdStr) : null,
        };
    }

    private static OptimizationRunRecord ReadOptimizationRun(SqliteDataReader reader) => new()
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
        DataSubscriptions = [], // loaded separately
        Trials = [], // loaded separately
    };

    private static IReadOnlyList<DataSubscriptionRecord> LoadBacktestSubscriptions(SqliteConnection conn, Guid backtestRunId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT asset_name, exchange, timeframe FROM backtest_data_subscriptions WHERE backtest_run_id = $id";
        cmd.Parameters.AddWithValue("$id", backtestRunId.ToString());

        var subs = new List<DataSubscriptionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            subs.Add(new DataSubscriptionRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        return subs;
    }

    private static IReadOnlyList<DataSubscriptionRecord> LoadOptimizationSubscriptions(SqliteConnection conn, Guid optimizationRunId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT asset_name, exchange, timeframe FROM optimization_data_subscriptions WHERE optimization_run_id = $id";
        cmd.Parameters.AddWithValue("$id", optimizationRunId.ToString());

        var subs = new List<DataSubscriptionRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            subs.Add(new DataSubscriptionRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        return subs;
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
