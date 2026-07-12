using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

public sealed partial class SqliteHistoryIndex
{
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
}
