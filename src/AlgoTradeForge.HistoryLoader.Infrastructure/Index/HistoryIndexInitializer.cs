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
    private const int CurrentVersion = 1;
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
            id            TEXT NOT NULL PRIMARY KEY,
            kind          TEXT NOT NULL,
            state         TEXT NOT NULL,
            progress_json TEXT NOT NULL DEFAULT '{}',
            error         TEXT NULL,
            created_at    TEXT NOT NULL,
            updated_at    TEXT NOT NULL
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

        // Startup sweep (spec §3.4): a job left 'running' by a crashed process can never finish.
        await using var sweepCmd = conn.CreateCommand();
        sweepCmd.CommandText = "UPDATE index_jobs SET state = 'interrupted' WHERE state = 'running'";
        await sweepCmd.ExecuteNonQueryAsync(ct);

        _done = true;
    }
}
