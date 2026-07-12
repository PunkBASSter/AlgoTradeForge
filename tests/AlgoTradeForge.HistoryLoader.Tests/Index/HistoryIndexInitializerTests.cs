using Microsoft.Data.Sqlite;
using Xunit;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class HistoryIndexInitializerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-index-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string DbPath => Path.Combine(_dir, "history-index.sqlite");
    private string ConnStr => $"Data Source={DbPath};Pooling=False";

    [Fact]
    public async Task EnsureCreated_CreatesAllTables()
    {
        var init = new HistoryIndexInitializer(DbPath);
        await init.EnsureCreated(Ct);

        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync(Ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) tables.Add(reader.GetString(0));

        Assert.Contains("assets", tables);
        Assert.Contains("feed_status", tables);
        Assert.Contains("month_partitions", tables);
        Assert.Contains("index_jobs", tables);
        Assert.Contains("schema_version", tables);
    }

    [Fact]
    public async Task EnsureCreated_MarksRunningJobsInterrupted()
    {
        var init = new HistoryIndexInitializer(DbPath);
        await init.EnsureCreated(Ct);

        await using (var conn = new SqliteConnection(ConnStr))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO index_jobs (id, kind, state, progress_json, created_at, updated_at)
                VALUES ('j1', 'rebuild', 'running', '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
                """;
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var second = new HistoryIndexInitializer(DbPath);
        await second.EnsureCreated(Ct);

        await using var check = new SqliteConnection(ConnStr);
        await check.OpenAsync(Ct);
        await using var checkCmd = check.CreateCommand();
        checkCmd.CommandText = "SELECT state FROM index_jobs WHERE id = 'j1'";
        Assert.Equal("interrupted", (string)(await checkCmd.ExecuteScalarAsync(Ct))!);
    }

    [Fact]
    public async Task EnsureCreated_OnV1Db_MigratesToV2_AndIsIdempotent()
    {
        var path = Path.Combine(_dir, "idx.sqlite");
        // Seed a v1 database: schema_version=1, index_jobs WITHOUT the new columns.
        await using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE schema_version (version INTEGER NOT NULL);
                INSERT INTO schema_version (version) VALUES (1);
                CREATE TABLE index_jobs (id TEXT PRIMARY KEY, kind TEXT NOT NULL, state TEXT NOT NULL,
                    progress_json TEXT NOT NULL DEFAULT '{}', error TEXT NULL,
                    created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
                INSERT INTO index_jobs (id, kind, state, created_at, updated_at)
                    VALUES ('old1', 'index', 'complete', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z');
                """;
            await cmd.ExecuteNonQueryAsync(Ct);
        }

        var init = new HistoryIndexInitializer(path);
        await init.EnsureCreated(Ct);
        // Force a fresh initializer so the volatile _done flag can't mask a re-run throw.
        var init2 = new HistoryIndexInitializer(path);
        await init2.EnsureCreated(Ct);   // must NOT throw "duplicate column name"

        await using var verify = new SqliteConnection($"Data Source={path};Pooling=False");
        await verify.OpenAsync(Ct);
        await using var check = verify.CreateCommand();
        check.CommandText = "SELECT version FROM schema_version";
        Assert.Equal(2L, (long)(await check.ExecuteScalarAsync(Ct))!);
        check.CommandText = "SELECT COUNT(*) FROM index_jobs WHERE id='old1'";   // legacy row survives
        Assert.Equal(1L, (long)(await check.ExecuteScalarAsync(Ct))!);
        SqliteConnection.ClearAllPools();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
