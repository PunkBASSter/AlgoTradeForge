using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

/// <summary>
/// connectionString override exists for tests (Pooling=False); production resolves it from the
/// initializer. Every op awaits EnsureCreated first (volatile-flag fast path) — endpoints can
/// hit the index before IndexMaintenanceService's ExecuteAsync has run on a cold start.
/// All writes serialize behind a single non-reentrant _writeGate (SemaphoreSlim(1,1)).
/// Read methods are ungated — WAL permits concurrent readers.
/// </summary>
public sealed partial class SqliteHistoryIndex(
    HistoryIndexInitializer initializer,
    string? connectionString = null,
    int maxEventsPerJob = 500) : IHistoryIndex
{
    private readonly string _connectionString = connectionString ?? initializer.ConnectionString;
    // Process-wide, NON-reentrant; HistoryLoader is single-host.
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    internal readonly int _maxEventsPerJob = maxEventsPerJob;

    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        await initializer.EnsureCreated(ct);
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return conn;
    }

    public async Task UpsertAsset(AssetIndexRow row, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO assets (exchange, dir, symbol, type, manifest_json, indexed_at)
            VALUES ($ex, $dir, $sym, $type, $manifest, $now)
            ON CONFLICT(exchange, dir) DO UPDATE SET
                symbol = $sym, type = $type, manifest_json = $manifest, indexed_at = $now
            """;
        cmd.Parameters.AddWithValue("$ex", row.Exchange);
        cmd.Parameters.AddWithValue("$dir", row.Dir);
        cmd.Parameters.AddWithValue("$sym", row.Symbol);
        cmd.Parameters.AddWithValue("$type", row.Type);
        cmd.Parameters.AddWithValue("$manifest", row.ManifestJson);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task RemoveAssetCore(SqliteConnection conn, string exchange, string dir, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.Parameters.AddWithValue("$ex", exchange);
        cmd.Parameters.AddWithValue("$dir", dir);

        cmd.CommandText = "DELETE FROM month_partitions WHERE exchange = $ex AND dir = $dir";
        await cmd.ExecuteNonQueryAsync(ct);

        cmd.CommandText = "DELETE FROM feed_status WHERE exchange = $ex AND dir = $dir";
        await cmd.ExecuteNonQueryAsync(ct);

        cmd.CommandText = "DELETE FROM assets WHERE exchange = $ex AND dir = $dir";
        await cmd.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }

    public async Task RemoveAsset(string exchange, string dir, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await RemoveAssetCore(conn, exchange, dir, ct);
    }

    public async Task<IReadOnlyList<AssetIndexRow>> ListAssets(string? exchange = null, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = exchange is null
            ? "SELECT exchange, dir, symbol, type, manifest_json FROM assets ORDER BY exchange, dir"
            : "SELECT exchange, dir, symbol, type, manifest_json FROM assets WHERE exchange = $ex COLLATE NOCASE ORDER BY exchange, dir";
        if (exchange is not null)
            cmd.Parameters.AddWithValue("$ex", exchange);

        var results = new List<AssetIndexRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new AssetIndexRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return results;
    }

    public async Task<AssetIndexRow?> GetAsset(string exchange, string dir, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT exchange, dir, symbol, type, manifest_json FROM assets WHERE exchange = $ex COLLATE NOCASE AND dir = $dir";
        cmd.Parameters.AddWithValue("$ex", exchange);
        cmd.Parameters.AddWithValue("$dir", dir);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new AssetIndexRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    public async Task UpsertFeedStatus(FeedStatusIndexRow row, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        // discovered_first_month is a phase-2 column — never touched here.
        cmd.CommandText = """
            INSERT INTO feed_status
                (exchange, dir, feed_name, interval, first_ts, last_ts, record_count, health, gaps_json, complete_months_json)
            VALUES ($ex, $dir, $feed, $iv, $fts, $lts, $rc, $health, $gaps, $cm)
            ON CONFLICT(exchange, dir, feed_name, interval) DO UPDATE SET
                first_ts = $fts, last_ts = $lts, record_count = $rc,
                health = $health, gaps_json = $gaps, complete_months_json = $cm
            """;
        cmd.Parameters.AddWithValue("$ex", row.Exchange);
        cmd.Parameters.AddWithValue("$dir", row.Dir);
        cmd.Parameters.AddWithValue("$feed", row.FeedName);
        cmd.Parameters.AddWithValue("$iv", row.Interval);
        cmd.Parameters.AddWithValue("$fts", row.FirstTs.HasValue ? (object)row.FirstTs.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$lts", row.LastTs.HasValue ? (object)row.LastTs.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("$rc", row.RecordCount);
        cmd.Parameters.AddWithValue("$health", row.Health);
        cmd.Parameters.AddWithValue("$gaps", row.GapsJson);
        cmd.Parameters.AddWithValue("$cm", row.CompleteMonthsJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<FeedStatusIndexRow>> GetFeedStatuses(string exchange, string dir, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT exchange, dir, feed_name, interval, first_ts, last_ts, record_count, health, gaps_json, complete_months_json
            FROM feed_status
            WHERE exchange = $ex COLLATE NOCASE AND dir = $dir COLLATE NOCASE
            ORDER BY feed_name, interval
            """;
        cmd.Parameters.AddWithValue("$ex", exchange);
        cmd.Parameters.AddWithValue("$dir", dir);

        var results = new List<FeedStatusIndexRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new FeedStatusIndexRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9)));
        }
        return results;
    }

    public async Task ReplaceMonths(string exchange, string dir, string feedName, string interval,
        IReadOnlyList<MonthPartitionRow> months, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.Parameters.AddWithValue("$ex", exchange);
        cmd.Parameters.AddWithValue("$dir", dir);
        cmd.Parameters.AddWithValue("$feed", feedName);
        cmd.Parameters.AddWithValue("$iv", interval);

        cmd.CommandText = "DELETE FROM month_partitions WHERE exchange = $ex AND dir = $dir AND feed_name = $feed AND interval = $iv";
        await cmd.ExecuteNonQueryAsync(ct);

        cmd.CommandText = """
            INSERT INTO month_partitions (exchange, dir, feed_name, interval, month, rows, file_len, file_mtime)
            VALUES ($ex, $dir, $feed, $iv, $month, $rows, $flen, $mtime)
            """;
        cmd.Parameters.Add("$month", SqliteType.Text);
        cmd.Parameters.Add("$rows", SqliteType.Integer);
        cmd.Parameters.Add("$flen", SqliteType.Integer);
        cmd.Parameters.Add("$mtime", SqliteType.Text);

        foreach (var m in months)
        {
            cmd.Parameters["$month"].Value = m.Month;
            cmd.Parameters["$rows"].Value = m.Rows;
            cmd.Parameters["$flen"].Value = m.FileLen;
            cmd.Parameters["$mtime"].Value = m.FileMtimeUtc;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<MonthPartitionRow>> GetMonths(string exchange, string dir, string feedName, string interval, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT month, rows, file_len, file_mtime
            FROM month_partitions
            WHERE exchange = $ex COLLATE NOCASE AND dir = $dir COLLATE NOCASE AND feed_name = $feed AND interval = $iv
            ORDER BY month
            """;
        cmd.Parameters.AddWithValue("$ex", exchange);
        cmd.Parameters.AddWithValue("$dir", dir);
        cmd.Parameters.AddWithValue("$feed", feedName);
        cmd.Parameters.AddWithValue("$iv", interval);

        var results = new List<MonthPartitionRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new MonthPartitionRow(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3)));
        return results;
    }

    public async Task<IReadOnlyList<(string FeedName, string Interval)>> ListFeedKeys(string exchange, string dir, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT feed_name, interval FROM (
                SELECT feed_name, interval FROM feed_status WHERE exchange = $ex COLLATE NOCASE AND dir = $dir COLLATE NOCASE
                UNION
                SELECT feed_name, interval FROM month_partitions WHERE exchange = $ex COLLATE NOCASE AND dir = $dir COLLATE NOCASE
            )
            """;
        cmd.Parameters.AddWithValue("$ex", exchange);
        cmd.Parameters.AddWithValue("$dir", dir);

        var results = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetString(1)));
        return results;
    }

    public async Task UpsertInstrumentMeta(IReadOnlyList<InstrumentMetaRow> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = (SqliteTransaction)tx;
        cmd.CommandText = """
            INSERT INTO instrument_meta (exchange, dir, price_decimals, qty_decimals, tick_size, fetched_at)
            VALUES ($ex, $dir, $pd, $qd, $ts, $fa)
            ON CONFLICT(exchange, dir) DO UPDATE SET
                price_decimals = excluded.price_decimals,
                qty_decimals   = excluded.qty_decimals,
                tick_size      = excluded.tick_size,
                fetched_at     = excluded.fetched_at
            """;
        cmd.Parameters.Add("$ex", SqliteType.Text);
        cmd.Parameters.Add("$dir", SqliteType.Text);
        cmd.Parameters.Add("$pd", SqliteType.Integer);
        cmd.Parameters.Add("$qd", SqliteType.Integer);
        cmd.Parameters.Add("$ts", SqliteType.Text);
        cmd.Parameters.Add("$fa", SqliteType.Text);
        foreach (var row in rows)
        {
            cmd.Parameters["$ex"].Value = row.Exchange;
            cmd.Parameters["$dir"].Value = row.Dir;
            cmd.Parameters["$pd"].Value = row.PriceDecimals;
            cmd.Parameters["$qd"].Value = row.QtyDecimals;
            cmd.Parameters["$ts"].Value = row.TickSize;
            cmd.Parameters["$fa"].Value = row.FetchedAtUtc;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<InstrumentMetaRow>> ListInstrumentMeta(string? exchange = null, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = exchange is null
            ? "SELECT exchange, dir, price_decimals, qty_decimals, tick_size, fetched_at FROM instrument_meta ORDER BY exchange, dir"
            : "SELECT exchange, dir, price_decimals, qty_decimals, tick_size, fetched_at FROM instrument_meta WHERE exchange = $ex COLLATE NOCASE ORDER BY dir";
        if (exchange is not null)
            cmd.Parameters.AddWithValue("$ex", exchange);

        var results = new List<InstrumentMetaRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new InstrumentMetaRow(
                reader.GetString(0), reader.GetString(1),
                reader.GetInt32(2), reader.GetInt32(3),
                reader.GetString(4), reader.GetString(5)));
        return results;
    }

    public async Task SetDiscoveredFirstMonth(string exchange, string dir, string feedName, string interval, string month, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO feed_status (exchange, dir, feed_name, interval, discovered_first_month)
            VALUES ($ex, $dir, $feed, $iv, $m)
            ON CONFLICT(exchange, dir, feed_name, interval)
            DO UPDATE SET discovered_first_month = excluded.discovered_first_month
            """;
        cmd.Parameters.AddWithValue("$ex", exchange);
        cmd.Parameters.AddWithValue("$dir", dir);
        cmd.Parameters.AddWithValue("$feed", feedName);
        cmd.Parameters.AddWithValue("$iv", interval);
        cmd.Parameters.AddWithValue("$m", month);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DiscoveredFirstMonthRow>> ListDiscoveredFirstMonths(CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT exchange, dir, feed_name, interval, discovered_first_month
            FROM feed_status WHERE discovered_first_month IS NOT NULL
            """;
        var rows = new List<DiscoveredFirstMonthRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add(new DiscoveredFirstMonthRow(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return rows;
    }

    public async Task<IReadOnlyList<(string Exchange, string Dir, string FeedName, string Interval)>> ListAllFeedKeys(CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT exchange, dir, feed_name, interval FROM (
                SELECT exchange, dir, feed_name, interval FROM feed_status
                UNION
                SELECT exchange, dir, feed_name, interval FROM month_partitions
            )
            """;
        var keys = new List<(string, string, string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            keys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return keys;
    }

    public async Task PruneFeedData(string exchange, string dir,
        IReadOnlyCollection<(string FeedName, string Interval)> keep, CancellationToken ct = default)
    {
        // Read outside the gate — WAL allows concurrent readers; TOCTOU is benign (idempotent deletes).
        var existing = await ListFeedKeys(exchange, dir, ct);
        var toDelete = existing.Where(k => !keep.Contains(k)).ToList();
        if (toDelete.Count == 0) return;

        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$ex", exchange);
        cmd.Parameters.AddWithValue("$dir", dir);
        cmd.Parameters.Add("$feed", SqliteType.Text);
        cmd.Parameters.Add("$iv", SqliteType.Text);

        foreach (var (feedName, interval) in toDelete)
        {
            cmd.Parameters["$feed"].Value = feedName;
            cmd.Parameters["$iv"].Value = interval;

            cmd.CommandText = "DELETE FROM feed_status WHERE exchange = $ex AND dir = $dir AND feed_name = $feed AND interval = $iv";
            await cmd.ExecuteNonQueryAsync(ct);

            cmd.CommandText = "DELETE FROM month_partitions WHERE exchange = $ex AND dir = $dir AND feed_name = $feed AND interval = $iv";
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task PruneAssetsNotIn(IReadOnlyCollection<(string Exchange, string Dir)> keep, CancellationToken ct = default)
    {
        // Read outside the gate; gate is acquired once for all removals to avoid nested acquisition.
        var all = await ListAssets(ct: ct);
        var toRemove = all.Where(a => !keep.Contains((a.Exchange, a.Dir))).ToList();
        if (toRemove.Count == 0) return;

        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        foreach (var asset in toRemove)
            await RemoveAssetCore(conn, asset.Exchange, asset.Dir, ct);
    }

    public async Task<bool> IsEmpty(CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM assets)";
        var result = await cmd.ExecuteScalarAsync(ct);
        // SQLite returns 1L for true, 0L for false.
        return Convert.ToInt64(result) == 1L;
    }

    public async Task<string> CreateJob(string kind, CancellationToken ct = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");

        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO index_jobs (id, kind, state, progress_json, created_at, updated_at)
            VALUES ($id, $kind, 'queued', '{}', $now, $now)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task UpdateJob(string id, string state, string? progressJson = null, string? error = null, CancellationToken ct = default)
    {
        using var _ = await _writeGate.LockAsync(ct);
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE index_jobs SET
                state = $state,
                updated_at = $now,
                progress_json = COALESCE($p, progress_json),
                error = COALESCE($err, error)
            WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$p", progressJson is not null ? (object)progressJson : DBNull.Value);
        cmd.Parameters.AddWithValue("$err", error is not null ? (object)error : DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IndexJobRow?> GetJob(string id, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, state, progress_json, error, feed_key, cancel_requested, touched_json, request_json
            FROM index_jobs WHERE id = $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return await ReadJobRow(cmd, ct);
    }

    public async Task<IndexJobRow?> GetActiveJob(string kind, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, state, progress_json, error, feed_key, cancel_requested, touched_json, request_json
            FROM index_jobs
            WHERE kind = $kind AND state = 'running'
            ORDER BY created_at DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        return await ReadJobRow(cmd, ct);
    }

    public async Task<IndexJobRow?> GetLastJob(string kind, CancellationToken ct = default)
    {
        await using var conn = await Open(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, kind, state, progress_json, error, feed_key, cancel_requested, touched_json, request_json
            FROM index_jobs
            WHERE kind = $kind
            ORDER BY created_at DESC LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        return await ReadJobRow(cmd, ct);
    }

    internal static async Task<IndexJobRow?> ReadJobRow(SqliteCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new IndexJobRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetBoolean(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }
}
