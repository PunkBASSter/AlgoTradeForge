using AlgoTradeForge.Storage.Threading;
using AlgoTradeForge.HistoryLoader.Application.Index;
using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

/// <summary>
/// Owns the history-index.sqlite schema. Separate instance from the main WebApi's runs.sqlite —
/// this DB is HistoryLoader-private, derived from disk, and rebuildable at any time (spec §3.3).
/// </summary>
public sealed class HistoryIndexInitializer(string dbPath)
{
    private const int CurrentVersion = 2;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _done;

    public string ConnectionString { get; } = $"Data Source={dbPath}";

    public static string ResolvePath(IndexOptions options) =>
        options.Path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AlgoTradeForge", "history-index.sqlite");

    private const string Schema = """
        PRAGMA journal_mode=WAL;

        CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS assets (
            exchange      TEXT NOT NULL,
            dir           TEXT NOT NULL,
            symbol        TEXT NOT NULL,
            type          TEXT NOT NULL,
            manifest_json TEXT NOT NULL,
            indexed_at    TEXT NOT NULL,
            PRIMARY KEY (exchange, dir)
        );

        CREATE TABLE IF NOT EXISTS feed_status (
            exchange               TEXT NOT NULL,
            dir                    TEXT NOT NULL,
            feed_name              TEXT NOT NULL,
            interval               TEXT NOT NULL DEFAULT '',
            first_ts               INTEGER NULL,
            last_ts                INTEGER NULL,
            record_count           INTEGER NOT NULL DEFAULT 0,
            health                 TEXT NOT NULL DEFAULT 'Healthy',
            gaps_json              TEXT NOT NULL DEFAULT '[]',
            complete_months_json   TEXT NOT NULL DEFAULT '[]',
            discovered_first_month TEXT NULL,
            PRIMARY KEY (exchange, dir, feed_name, interval)
        );

        CREATE TABLE IF NOT EXISTS month_partitions (
            exchange   TEXT NOT NULL,
            dir        TEXT NOT NULL,
            feed_name  TEXT NOT NULL,
            interval   TEXT NOT NULL DEFAULT '',
            month      TEXT NOT NULL,
            rows       INTEGER NOT NULL,
            file_len   INTEGER NOT NULL,
            file_mtime TEXT NOT NULL,
            PRIMARY KEY (exchange, dir, feed_name, interval, month)
        );

        CREATE INDEX IF NOT EXISTS ix_mp_asset ON month_partitions(exchange, dir);

        CREATE TABLE IF NOT EXISTS index_jobs (
            id               TEXT NOT NULL PRIMARY KEY,
            kind             TEXT NOT NULL,
            state            TEXT NOT NULL,
            progress_json    TEXT NOT NULL DEFAULT '{}',
            error            TEXT NULL,
            created_at       TEXT NOT NULL,
            updated_at       TEXT NOT NULL,
            feed_key         TEXT NULL,
            cancel_requested INTEGER NOT NULL DEFAULT 0,
            touched_json     TEXT NOT NULL DEFAULT '[]',
            request_json     TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS job_events (
            job_id       TEXT    NOT NULL,
            seq          INTEGER NOT NULL,
            kind         TEXT    NOT NULL,
            payload_json TEXT    NOT NULL,
            created_at   TEXT    NOT NULL,
            PRIMARY KEY (job_id, seq)
        );

        CREATE TABLE IF NOT EXISTS instrument_meta (
            exchange       TEXT NOT NULL,
            dir            TEXT NOT NULL,
            price_decimals INTEGER NOT NULL,
            qty_decimals   INTEGER NOT NULL,
            tick_size      TEXT NOT NULL,
            fetched_at     TEXT NOT NULL,
            PRIMARY KEY (exchange, dir)
        );
        """;

    public async Task EnsureCreated(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _done)) return;
        using var _ = await _gate.LockAsync(ct);
        if (_done) return;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);

        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);

        await using var schemaCmd = conn.CreateCommand();
        schemaCmd.CommandText = Schema;
        await schemaCmd.ExecuteNonQueryAsync(ct);

        await using var versionCmd = conn.CreateCommand();
        versionCmd.CommandText = $"""
            INSERT INTO schema_version (version)
            SELECT {CurrentVersion}
            WHERE NOT EXISTS (SELECT 1 FROM schema_version)
            """;
        await versionCmd.ExecuteNonQueryAsync(ct);

        // Version-guarded migration: ALTER TABLE has no IF NOT EXISTS, so guarded by stored version.
        await using (var readVer = conn.CreateCommand())
        {
            readVer.CommandText = "SELECT version FROM schema_version LIMIT 1";
            var stored = Convert.ToInt32(await readVer.ExecuteScalarAsync(ct));
            if (stored < 2)
            {
                await using var tx = await conn.BeginTransactionAsync(ct);
                await using var mig = conn.CreateCommand();
                mig.Transaction = (SqliteTransaction)tx;
                mig.CommandText = """
                    ALTER TABLE index_jobs ADD COLUMN feed_key TEXT NULL;
                    ALTER TABLE index_jobs ADD COLUMN cancel_requested INTEGER NOT NULL DEFAULT 0;
                    ALTER TABLE index_jobs ADD COLUMN touched_json TEXT NOT NULL DEFAULT '[]';
                    ALTER TABLE index_jobs ADD COLUMN request_json TEXT NULL;
                    """;
                await mig.ExecuteNonQueryAsync(ct);
                mig.CommandText = "UPDATE schema_version SET version = 2";
                await mig.ExecuteNonQueryAsync(ct);
                await tx.CommitAsync(ct);
            }
        }

        // Placed after migration so ux_jobs_active_feedkey (which references feed_key) is only
        // attempted once feed_key is guaranteed present on both fresh and migrated databases.
        await using (var idx = conn.CreateCommand())
        {
            idx.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_jobs_kind_state ON index_jobs(kind, state);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active_feedkey
                    ON index_jobs(feed_key) WHERE feed_key IS NOT NULL AND state IN ('queued','running');
                """;
            await idx.ExecuteNonQueryAsync(ct);
        }

        // Startup sweep (spec §3.4): a job left 'running' by a crashed process can never finish.
        await using var sweepCmd = conn.CreateCommand();
        sweepCmd.CommandText = "UPDATE index_jobs SET state = 'interrupted' WHERE state = 'running'";
        await sweepCmd.ExecuteNonQueryAsync(ct);

        _done = true;
    }
}
