using System.Text.Json;
using AlgoTradeForge.Application.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.Persistence;

public sealed class SqliteValidationRepository : IValidationRepository, IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public SqliteValidationRepository(IOptions<RunStorageOptions> options)
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

    public async Task InsertPlaceholderAsync(ValidationRunRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO validation_runs (
                id, optimization_run_id, strategy_name, strategy_version,
                started_at, status, threshold_profile_name, threshold_profile_json,
                candidates_in, invocation_count
            ) VALUES (
                $id, $optId, $stratName, $stratVer,
                $startedAt, $status, $profileName, $profileJson,
                $candidatesIn, $invocationCount
            )
            """;

        cmd.Parameters.AddWithValue("$id", record.Id.ToString());
        cmd.Parameters.AddWithValue("$optId", record.OptimizationRunId.ToString());
        cmd.Parameters.AddWithValue("$stratName", record.StrategyName);
        cmd.Parameters.AddWithValue("$stratVer", (object?)record.StrategyVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$startedAt", record.StartedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$status", record.Status);
        cmd.Parameters.AddWithValue("$profileName", record.ThresholdProfileName);
        cmd.Parameters.AddWithValue("$profileJson", (object?)record.ThresholdProfileJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$candidatesIn", record.CandidatesIn);
        cmd.Parameters.AddWithValue("$invocationCount", record.InvocationCount);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAsync(ValidationRunRecord record, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);
        using var tx = conn.BeginTransaction();

        // Update the validation run
        await using var updateCmd = conn.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = """
            UPDATE validation_runs SET
                completed_at = $completedAt,
                duration_ms = $durationMs,
                status = $status,
                threshold_profile_json = $profileJson,
                candidates_in = $candidatesIn,
                candidates_out = $candidatesOut,
                composite_score = $compositeScore,
                verdict = $verdict,
                verdict_summary = $verdictSummary,
                error_message = $errorMsg
            WHERE id = $id
            """;

        updateCmd.Parameters.AddWithValue("$id", record.Id.ToString());
        updateCmd.Parameters.AddWithValue("$completedAt",
            record.CompletedAt.HasValue ? record.CompletedAt.Value.ToString("O") : DBNull.Value);
        updateCmd.Parameters.AddWithValue("$durationMs", record.DurationMs);
        updateCmd.Parameters.AddWithValue("$status", record.Status);
        updateCmd.Parameters.AddWithValue("$profileJson", (object?)record.ThresholdProfileJson ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("$candidatesIn", record.CandidatesIn);
        updateCmd.Parameters.AddWithValue("$candidatesOut", record.CandidatesOut);
        updateCmd.Parameters.AddWithValue("$compositeScore", record.CompositeScore);
        updateCmd.Parameters.AddWithValue("$verdict", record.Verdict);
        updateCmd.Parameters.AddWithValue("$verdictSummary", (object?)record.VerdictSummary ?? DBNull.Value);
        updateCmd.Parameters.AddWithValue("$errorMsg", (object?)record.ErrorMessage ?? DBNull.Value);

        await updateCmd.ExecuteNonQueryAsync(ct);

        // Insert stage results
        foreach (var stage in record.StageResults)
        {
            await using var stageCmd = conn.CreateCommand();
            stageCmd.Transaction = tx;
            stageCmd.CommandText = """
                INSERT INTO validation_stage_results (
                    validation_run_id, stage_number, stage_name,
                    candidates_in, candidates_out, duration_ms,
                    candidate_verdicts_json
                ) VALUES (
                    $valId, $stageNum, $stageName,
                    $candidatesIn, $candidatesOut, $durationMs,
                    $verdictsJson
                )
                """;

            stageCmd.Parameters.AddWithValue("$valId", stage.ValidationRunId.ToString());
            stageCmd.Parameters.AddWithValue("$stageNum", stage.StageNumber);
            stageCmd.Parameters.AddWithValue("$stageName", stage.StageName);
            stageCmd.Parameters.AddWithValue("$candidatesIn", stage.CandidatesIn);
            stageCmd.Parameters.AddWithValue("$candidatesOut", stage.CandidatesOut);
            stageCmd.Parameters.AddWithValue("$durationMs", stage.DurationMs);
            stageCmd.Parameters.AddWithValue("$verdictsJson", (object?)stage.CandidateVerdictsJson ?? DBNull.Value);

            await stageCmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }

    public async Task<ValidationRunRecord?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM validation_runs WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var record = ReadValidationRun(reader);

        // Load stage results
        await using var stageCmd = conn.CreateCommand();
        stageCmd.CommandText = """
            SELECT * FROM validation_stage_results
            WHERE validation_run_id = $valId
            ORDER BY stage_number
            """;
        stageCmd.Parameters.AddWithValue("$valId", id.ToString());

        var stages = new List<StageResultRecord>();
        await using var stageReader = await stageCmd.ExecuteReaderAsync(ct);
        while (await stageReader.ReadAsync(ct))
        {
            stages.Add(ReadStageResult(stageReader));
        }

        return record with { StageResults = stages };
    }

    public async Task<int> CountByOptimizationIdAsync(Guid optimizationId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM validation_runs WHERE optimization_run_id = $optId";
        cmd.Parameters.AddWithValue("$optId", optimizationId.ToString());

        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result);
    }

    public async Task<IReadOnlyList<ValidationRunRecord>> ListAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM validation_runs ORDER BY started_at DESC";

        var results = new List<ValidationRunRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadValidationRun(reader));
        }

        return results;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = await CreateConnectionAsync(ct);
        using var tx = conn.BeginTransaction();

        // Delete stage results first
        await using var deleteStagesCmd = conn.CreateCommand();
        deleteStagesCmd.Transaction = tx;
        deleteStagesCmd.CommandText = "DELETE FROM validation_stage_results WHERE validation_run_id = $id";
        deleteStagesCmd.Parameters.AddWithValue("$id", id.ToString());
        await deleteStagesCmd.ExecuteNonQueryAsync(ct);

        // Delete validation run
        await using var deleteCmd = conn.CreateCommand();
        deleteCmd.Transaction = tx;
        deleteCmd.CommandText = "DELETE FROM validation_runs WHERE id = $id";
        deleteCmd.Parameters.AddWithValue("$id", id.ToString());
        var rows = await deleteCmd.ExecuteNonQueryAsync(ct);

        tx.Commit();
        return rows > 0;
    }

    private static ValidationRunRecord ReadValidationRun(SqliteDataReader r)
    {
        var completedAtStr = r["completed_at"] as string;
        return new ValidationRunRecord
        {
            Id = Guid.Parse(r["id"].ToString()!),
            OptimizationRunId = Guid.Parse(r["optimization_run_id"].ToString()!),
            StrategyName = r["strategy_name"].ToString()!,
            StrategyVersion = r["strategy_version"] as string,
            StartedAt = DateTimeOffset.Parse(r["started_at"].ToString()!),
            CompletedAt = string.IsNullOrEmpty(completedAtStr) ? null : DateTimeOffset.Parse(completedAtStr),
            DurationMs = Convert.ToInt64(r["duration_ms"]),
            Status = r["status"].ToString()!,
            ThresholdProfileName = r["threshold_profile_name"].ToString()!,
            ThresholdProfileJson = r["threshold_profile_json"] as string,
            CandidatesIn = Convert.ToInt32(r["candidates_in"]),
            CandidatesOut = Convert.ToInt32(r["candidates_out"]),
            CompositeScore = Convert.ToDouble(r["composite_score"]),
            Verdict = r["verdict"].ToString()!,
            VerdictSummary = r["verdict_summary"] as string,
            InvocationCount = Convert.ToInt32(r["invocation_count"]),
            ErrorMessage = r["error_message"] as string,
        };
    }

    private static StageResultRecord ReadStageResult(SqliteDataReader r)
    {
        return new StageResultRecord
        {
            ValidationRunId = Guid.Parse(r["validation_run_id"].ToString()!),
            StageNumber = Convert.ToInt32(r["stage_number"]),
            StageName = r["stage_name"].ToString()!,
            CandidatesIn = Convert.ToInt32(r["candidates_in"]),
            CandidatesOut = Convert.ToInt32(r["candidates_out"]),
            DurationMs = Convert.ToInt64(r["duration_ms"]),
            CandidateVerdictsJson = r["candidate_verdicts_json"] as string,
        };
    }
}
