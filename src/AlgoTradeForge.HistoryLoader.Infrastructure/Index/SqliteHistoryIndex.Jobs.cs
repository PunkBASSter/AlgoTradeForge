using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

public sealed partial class SqliteHistoryIndex
{
    public async Task<FeedGateOutcome> TryAcquireFeedGate(string kind, string feedKey, string progressJson, string requestJson, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");

        await using var insert = conn.CreateCommand();
        // Check-and-insert is atomic in-process under _writeGate; ux_jobs_active_feedkey is the DB backstop if the guard races.
        insert.CommandText = """
            INSERT INTO index_jobs (id, kind, state, progress_json, feed_key, cancel_requested, touched_json, request_json, created_at, updated_at)
            SELECT $id, $kind, 'queued', $p, $fk, 0, '[]', $req, $now, $now
            WHERE NOT EXISTS (SELECT 1 FROM index_jobs WHERE feed_key=$fk AND state IN ('queued','running'))
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$p", progressJson);
        insert.Parameters.AddWithValue("$fk", feedKey);
        insert.Parameters.AddWithValue("$req", requestJson);
        insert.Parameters.AddWithValue("$now", now);
        var rows = await insert.ExecuteNonQueryAsync(ct);
        if (rows == 1) return new FeedGateOutcome.Acquired(id);

        await using var owner = conn.CreateCommand();
        owner.CommandText = "SELECT id FROM index_jobs WHERE feed_key=$fk AND state IN ('queued','running') LIMIT 1";
        owner.Parameters.AddWithValue("$fk", feedKey);
        var existing = (string?)await owner.ExecuteScalarAsync(ct);
        return new FeedGateOutcome.Busy(existing ?? "unknown");
    }

    public async Task<int> AppendJobEvent(string jobId, string eventKind, string payloadJson, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        // seq allocation is safe under the process-wide write gate; PK(job_id,seq) is the backstop.
        cmd.CommandText = """
            INSERT INTO job_events (job_id, seq, kind, payload_json, created_at)
            VALUES ($id, (SELECT COALESCE(MAX(seq),0)+1 FROM job_events WHERE job_id=$id), $k, $p, $now)
            RETURNING seq
            """;
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$k", eventKind);
        cmd.Parameters.AddWithValue("$p", payloadJson);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        var seq = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

        // §S6 cap: keep ALL lifecycle events + only the most recent N 'progress' events per job.
        if (eventKind == "progress")
        {
            await using var trim = conn.CreateCommand();
            trim.CommandText = """
                DELETE FROM job_events WHERE job_id=$id AND kind='progress'
                  AND seq NOT IN (SELECT seq FROM job_events WHERE job_id=$id AND kind='progress'
                                  ORDER BY seq DESC LIMIT $cap)
                """;
            trim.Parameters.AddWithValue("$id", jobId);
            trim.Parameters.AddWithValue("$cap", _maxEventsPerJob);
            await trim.ExecuteNonQueryAsync(ct);
        }
        return seq;
    }

    public async Task<IReadOnlyList<JobEventRow>> GetJobEventsAfter(string jobId, int afterSeq, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT seq, kind, payload_json, created_at
            FROM job_events WHERE job_id=$id AND seq>$after ORDER BY seq
            """;
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$after", afterSeq);

        var results = new List<JobEventRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new JobEventRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return results;
    }

    public async Task<int> GetLastEventSeq(string jobId, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(seq),0) FROM job_events WHERE job_id=$id";
        cmd.Parameters.AddWithValue("$id", jobId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<IndexJobRow>> ListJobs(string? kind, string? state, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (kind is not null) { conditions.Add("kind = $kind"); cmd.Parameters.AddWithValue("$kind", kind); }
        if (state is not null) { conditions.Add("state = $state"); cmd.Parameters.AddWithValue("$state", state); }

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"""
            SELECT id, kind, state, progress_json, error, feed_key, cancel_requested, touched_json, request_json
            FROM index_jobs {where}
            ORDER BY created_at DESC
            """;

        var results = new List<IndexJobRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new IndexJobRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetBoolean(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return results;
    }

    public async Task RequestCancel(string jobId, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE index_jobs SET cancel_requested=1, updated_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SetTouched(string jobId, string feedKey, string month, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new[] { new { feedKey, month } });
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE index_jobs SET touched_json=$j, updated_at=$now WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$j", json);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<InterruptedJobRow>> ListInterruptedJobs(CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, feed_key, touched_json FROM index_jobs WHERE state='interrupted'";
        var results = new List<InterruptedJobRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new InterruptedJobRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3)));
        return results;
    }

    public async Task DeleteJob(string jobId, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.CommandText = "DELETE FROM job_events WHERE job_id=$id";
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = "DELETE FROM index_jobs WHERE id=$id";
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<int> DeleteTerminalJobsBefore(DateTimeOffset cutoffUtc, CancellationToken ct = default)
    {
        // Bind as UtcDateTime.ToString("O") → "…Z" to string-compare correctly against stored "…Z" timestamps.
        var cutoff = cutoffUtc.UtcDateTime.ToString("O");
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.Parameters.AddWithValue("$cutoff", cutoff);
        cmd.CommandText = """
            DELETE FROM job_events WHERE job_id IN (
                SELECT id FROM index_jobs
                WHERE state IN ('complete','error','cancelled') AND updated_at < $cutoff)
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = """
            DELETE FROM index_jobs
            WHERE state IN ('complete','error','cancelled') AND updated_at < $cutoff
            """;
        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
        return deleted;
    }
}
