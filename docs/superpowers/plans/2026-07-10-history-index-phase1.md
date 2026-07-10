# History Metadata Index (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the recursive filesystem crawl behind HistoryLoader's catalog/coverage endpoints with a rebuildable SQLite metadata index (`history-index.sqlite`), maintained incrementally from existing write seams, with full rebuild as a tracked job.

**Architecture:** Disk stays the source of truth (feeds.json + CSV partitions); the index is derived and rebuildable (spec §3.3, D2). All feed writes already funnel status updates through `IFeedStatusStore.Save` and manifest mutations through `ISchemaManager.ManifestChanged` — a decorator + one event subscription feed a single-writer maintenance queue that upserts index rows. Catalog and coverage endpoints become SELECTs; the coverage predicate is extracted into a pure function so index-backed reads keep byte-identical semantics with the current file-reading path.

**Tech Stack:** C# 14 / .NET 10, ASP.NET Core minimal APIs, `Microsoft.Data.Sqlite` 9.0.4 (new package ref in HistoryLoader.Infrastructure only), xUnit + NSubstitute.

**Spec:** `docs/superpowers/specs/2026-07-10-declarative-data-management-design.md` (§3.3 index, §4 phase 1). Phase-1 scope notes: `discovered_first_month` column is created but unused until phase 2; rebuild-job progress is **polled** (`GET /index/jobs/{id}`) — SSE unification arrives with the persistent-jobs work in phase 3.

## Global Constraints

- **ONE dotnet process at a time.** Never run build/test in parallel; wait for each command to finish (CLAUDE.md).
- Shell is Windows PowerShell 5.1 (`powershell.exe`, no `pwsh`); Bash tool also available. No `&&` chaining in PowerShell 5.1.
- Build: `dotnet build AlgoTradeForge.slnx`. Tests: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` (and other suites) — sequentially.
- **No `Async` suffix** on new async methods; `CancellationToken ct = default` on every async signature (Constitution v1.8.3).
- **One type per file**, named after the type (Constitution v1.9.0). Single-line records accompanying an interface may share its file.
- **Comments:** prefer none; only non-obvious algorithm/pitfall notes, terse (Constitution v1.8.4).
- **`using` over try/finally** for pure release; `SemaphoreSlim` via `SemaphoreSlimExtensions.LockAsync` (Constitution v1.9.1).
- **Background loops:** never `catch when (ex is not OperationCanceledException)`. Pattern: `catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw/return; }` then `catch (Exception ex) { log; continue; }`.
- **SQLite in tests:** `Pooling=False` in connection strings for temp DB files; `SqliteConnection.ClearAllPools()` in `Dispose()` before deleting files.
- **xUnit analyzers:** `Assert.Single(x)` not `Assert.Equal(1, x.Count)`; `Assert.Empty(x)` not `Assert.Equal(0, ...)`.
- **Wire contracts pinned:** JSON shapes of `GET /api/v1/exchanges`, `/assets`, `/exchanges/{ex}/assets`, `/coverage` must not change (snake_case, same property names). Only `POST /catalog/refresh` changes: `204` → `202 { "job_id": ... }`.
- **Git:** work on branch `feat/history-index-phase1`. Commit only the files the task touched, listed explicitly — never `git add -A` / `git add -u` (project rule: no bulk staging of unreviewed edits). Commit messages end with the `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` + `Claude-Session` trailer per harness rules.
- Existing tests that pin the old crawl behavior are **updated in the same task** that changes the behavior — never deferred as "pre-existing failures" (Constitution v1.9.2).

**Implementer notes (accepted trade-offs, don't "fix" silently):**
- `POST /catalog/refresh` has a benign race: two concurrent requests can create two rebuild jobs. Rebuild is idempotent, the second just re-scans; not worth a lock. Leave as is.
- Timestamps inside `SqliteHistoryIndex` use `DateTime.UtcNow`; if you touch those code paths anyway, prefer injecting `TimeProvider` (repo convention), but do not restructure for it.
- During a rebuild the single-reader queue blocks incremental updates for the crawl's duration — by design (single SQLite writer); the queued `FeedTouched` items apply right after. Document with a one-line comment on the worker loop.

---

### Task 1: Index options, package reference, schema initializer

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/IndexOptions.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs` (add one property)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/AlgoTradeForge.HistoryLoader.Infrastructure.csproj` (add package)
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/HistoryIndexInitializer.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/HistoryIndexInitializerTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `IndexOptions { string? Path, int DriftSweepMinutes }`; `HistoryLoaderOptions.Index`; `HistoryIndexInitializer(string dbPath)` with `string ConnectionString { get; }`, `Task EnsureCreated(CancellationToken ct = default)`, `static string ResolvePath(IndexOptions options)`. Tables: `schema_version`, `assets`, `feed_status`, `month_partitions`, `index_jobs`.

- [ ] **Step 1: Write the failing test**

```csharp
using Microsoft.Data.Sqlite;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class HistoryIndexInitializerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-index-").FullName;

    private string DbPath => Path.Combine(_dir, "history-index.sqlite");
    private string ConnStr => $"Data Source={DbPath};Pooling=False";

    [Fact]
    public async Task EnsureCreated_CreatesAllTables()
    {
        var init = new HistoryIndexInitializer(DbPath);
        await init.EnsureCreated();

        await using var conn = new SqliteConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

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
        await init.EnsureCreated();

        await using (var conn = new SqliteConnection(ConnStr))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO index_jobs (id, kind, state, progress_json, created_at, updated_at)
                VALUES ('j1', 'rebuild', 'running', '{}', '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z')
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var second = new HistoryIndexInitializer(DbPath);
        await second.EnsureCreated();

        await using var check = new SqliteConnection(ConnStr);
        await check.OpenAsync();
        await using var checkCmd = check.CreateCommand();
        checkCmd.CommandText = "SELECT state FROM index_jobs WHERE id = 'j1'";
        Assert.Equal("interrupted", (string)(await checkCmd.ExecuteScalarAsync())!);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~HistoryIndexInitializerTests"`
Expected: FAIL — `HistoryIndexInitializer` does not exist (compile error).

- [ ] **Step 3: Add the package reference and options**

In `AlgoTradeForge.HistoryLoader.Infrastructure.csproj`, inside the existing `<ItemGroup>` with `Microsoft.Extensions.Http`:

```xml
    <PackageReference Include="Microsoft.Data.Sqlite" Version="9.0.4" />
```

`src/AlgoTradeForge.HistoryLoader.Application/Index/IndexOptions.cs`:

```csharp
namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed class IndexOptions
{
    /// <summary>Null resolves to %LOCALAPPDATA%/AlgoTradeForge/history-index.sqlite.</summary>
    public string? Path { get; init; }

    public int DriftSweepMinutes { get; init; } = 60;
}
```

In `HistoryLoaderOptions.cs`, after the `Load` property add (and add `using AlgoTradeForge.HistoryLoader.Application.Index;`):

```csharp
    public IndexOptions Index { get; init; } = new();
```

- [ ] **Step 4: Write the initializer**

`src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/HistoryIndexInitializer.cs`:

```csharp
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
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~HistoryIndexInitializerTests"`
Expected: PASS (2 tests).

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Index/IndexOptions.cs src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/AlgoTradeForge.HistoryLoader.Infrastructure.csproj src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/HistoryIndexInitializer.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/HistoryIndexInitializerTests.cs
git commit -m "feat(index): history-index.sqlite schema initializer + IndexOptions"
```

---

### Task 2: Index repository — `IHistoryIndex` + `SqliteHistoryIndex`

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs` (interface + single-line row records)
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs`

**Interfaces:**
- Consumes: `HistoryIndexInitializer` (Task 1).
- Produces:

```csharp
public sealed record AssetIndexRow(string Exchange, string Dir, string Symbol, string Type, string ManifestJson);
public sealed record FeedStatusIndexRow(string Exchange, string Dir, string FeedName, string Interval, long? FirstTs, long? LastTs, long RecordCount, string Health, string GapsJson, string CompleteMonthsJson);
public sealed record MonthPartitionRow(string Month, long Rows, long FileLen, string FileMtimeUtc);
public sealed record IndexJobRow(string Id, string Kind, string State, string ProgressJson, string? Error);

public interface IHistoryIndex
{
    Task UpsertAsset(AssetIndexRow row, CancellationToken ct = default);
    Task RemoveAsset(string exchange, string dir, CancellationToken ct = default);
    Task<IReadOnlyList<AssetIndexRow>> ListAssets(string? exchange = null, CancellationToken ct = default);
    Task<AssetIndexRow?> GetAsset(string exchange, string dir, CancellationToken ct = default);

    Task UpsertFeedStatus(FeedStatusIndexRow row, CancellationToken ct = default);
    Task<IReadOnlyList<FeedStatusIndexRow>> GetFeedStatuses(string exchange, string dir, CancellationToken ct = default);

    Task ReplaceMonths(string exchange, string dir, string feedName, string interval,
        IReadOnlyList<MonthPartitionRow> months, CancellationToken ct = default);
    Task<IReadOnlyList<MonthPartitionRow>> GetMonths(string exchange, string dir, string feedName, string interval, CancellationToken ct = default);

    /// <summary>Distinct (feed_name, interval) across feed_status AND month_partitions — feeds
    /// with month rows but no status row (static equity data) must not be invisible to sweeps.</summary>
    Task<IReadOnlyList<(string FeedName, string Interval)>> ListFeedKeys(string exchange, string dir, CancellationToken ct = default);

    Task PruneFeedData(string exchange, string dir,
        IReadOnlyCollection<(string FeedName, string Interval)> keep, CancellationToken ct = default);
    Task PruneAssetsNotIn(IReadOnlyCollection<(string Exchange, string Dir)> keep, CancellationToken ct = default);

    Task<bool> IsEmpty(CancellationToken ct = default);

    Task<string> CreateJob(string kind, CancellationToken ct = default);
    Task UpdateJob(string id, string state, string? progressJson = null, string? error = null, CancellationToken ct = default);
    Task<IndexJobRow?> GetJob(string id, CancellationToken ct = default);
    Task<IndexJobRow?> GetActiveJob(string kind, CancellationToken ct = default);
    /// <summary>Latest job of the kind regardless of state — bootstrap uses it to resume an interrupted rebuild.</summary>
    Task<IndexJobRow?> GetLastJob(string kind, CancellationToken ct = default);
}
```

- [ ] **Step 1: Write the failing tests**

`tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs`:

```csharp
using Microsoft.Data.Sqlite;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class SqliteHistoryIndexTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-index-").FullName;
    private SqliteHistoryIndex _index = null!;

    public async Task InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await init.EnsureCreated();
        // Pooling=False so Dispose can delete the temp dir on Windows.
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task UpsertAsset_ThenGet_RoundTrips()
    {
        var row = new AssetIndexRow("binance", "BTCUSDT_perp", "BTCUSDT", "CryptoPerpetual", """{"feeds":{}}""");
        await _index.UpsertAsset(row);
        await _index.UpsertAsset(row with { Type = "Crypto" });   // second upsert overwrites

        var fetched = await _index.GetAsset("binance", "BTCUSDT_perp");
        Assert.NotNull(fetched);
        Assert.Equal("Crypto", fetched!.Type);
        Assert.Single(await _index.ListAssets());
        Assert.False(await _index.IsEmpty());
    }

    [Fact]
    public async Task ListAssets_FiltersByExchange_CaseInsensitive()
    {
        await _index.UpsertAsset(new("binance", "BTCUSDT", "BTCUSDT", "Crypto", "{}"));
        await _index.UpsertAsset(new("nasdaq", "NFLX", "NFLX", "Equity", "{}"));

        Assert.Single(await _index.ListAssets("BINANCE"));
        Assert.Equal(2, (await _index.ListAssets()).Count);
    }

    [Fact]
    public async Task ReplaceMonths_ReplacesWholeFeedSet()
    {
        await _index.ReplaceMonths("binance", "BTCUSDT", "candles", "1h",
            [new("2024-01", 744, 100, "m1"), new("2024-02", 696, 90, "m2")]);
        await _index.ReplaceMonths("binance", "BTCUSDT", "candles", "1h",
            [new("2024-02", 700, 95, "m3")]);

        var months = await _index.GetMonths("binance", "BTCUSDT", "candles", "1h");
        var only = Assert.Single(months);
        Assert.Equal("2024-02", only.Month);
        Assert.Equal(700, only.Rows);
    }

    [Fact]
    public async Task PruneFeedData_DeletesRowsOutsideKeepSet()
    {
        await _index.UpsertFeedStatus(new("binance", "BTCUSDT", "candles", "1h", 1, 2, 10, "Healthy", "[]", "[]"));
        await _index.UpsertFeedStatus(new("binance", "BTCUSDT", "mark-price", "1h", 1, 2, 10, "Healthy", "[]", "[]"));
        await _index.ReplaceMonths("binance", "BTCUSDT", "mark-price", "1h", [new("2024-01", 1, 1, "m")]);

        await _index.PruneFeedData("binance", "BTCUSDT", [("candles", "1h")]);

        var statuses = await _index.GetFeedStatuses("binance", "BTCUSDT");
        Assert.Single(statuses);
        Assert.Equal("candles", statuses[0].FeedName);
        Assert.Empty(await _index.GetMonths("binance", "BTCUSDT", "mark-price", "1h"));
    }

    [Fact]
    public async Task Jobs_CreateUpdateGet_AndActiveLookup()
    {
        var id = await _index.CreateJob("rebuild");
        var active = await _index.GetActiveJob("rebuild");
        Assert.Equal(id, active!.Id);
        Assert.Equal("running", active.State);

        await _index.UpdateJob(id, "completed", progressJson: """{"assets_done":5}""");
        var job = await _index.GetJob(id);
        Assert.Equal("completed", job!.State);
        Assert.Null(await _index.GetActiveJob("rebuild"));
        Assert.Equal(id, (await _index.GetLastJob("rebuild"))!.Id);   // latest regardless of state
    }

    [Fact]
    public async Task ListFeedKeys_UnionsStatusAndMonthRows()
    {
        await _index.UpsertFeedStatus(new("binance", "BTCUSDT", "candles", "1h", 1, 2, 10, "Healthy", "[]", "[]"));
        // Month rows without a status row — the static-equity shape.
        await _index.ReplaceMonths("binance", "BTCUSDT", "candles", "1d", [new("2024-01", 21, 1, "m")]);

        var keys = await _index.ListFeedKeys("binance", "BTCUSDT");
        Assert.Equal(2, keys.Count);
        Assert.Contains(("candles", "1h"), keys);
        Assert.Contains(("candles", "1d"), keys);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~SqliteHistoryIndexTests"`
Expected: FAIL — types don't exist.

- [ ] **Step 3: Write the interface file** (`Application/Index/IHistoryIndex.cs`) exactly as in the Interfaces block above, with the four records placed above the interface in the same file, namespace `AlgoTradeForge.HistoryLoader.Application.Index`.

- [ ] **Step 4: Write the repository**

`src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.cs` — pattern follows `SqliteRunRepository` (connection per op, parameterized commands). Key implementation notes; write out all members:

```csharp
using AlgoTradeForge.HistoryLoader.Application.Index;
using Microsoft.Data.Sqlite;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

/// <summary>
/// connectionString override exists for tests (Pooling=False); production resolves it from the
/// initializer. Every op awaits EnsureCreated first (volatile-flag fast path) — endpoints can
/// hit the index before IndexMaintenanceService's ExecuteAsync has run on a cold start.
/// </summary>
public sealed class SqliteHistoryIndex(HistoryIndexInitializer initializer, string? connectionString = null) : IHistoryIndex
{
    private readonly string _connectionString = connectionString ?? initializer.ConnectionString;

    private async Task<SqliteConnection> Open(CancellationToken ct)
    {
        await initializer.EnsureCreated(ct);
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task UpsertAsset(AssetIndexRow row, CancellationToken ct = default)
    {
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

    // RemoveAsset: DELETE from assets + feed_status + month_partitions WHERE exchange=$ex AND dir=$dir (three statements, one transaction).
    // ListAssets: SELECT ... [WHERE exchange = $ex COLLATE NOCASE] ORDER BY exchange, dir.
    // GetAsset: SELECT ... WHERE exchange = $ex COLLATE NOCASE AND dir = $dir.
    // UpsertFeedStatus: same ON CONFLICT pattern over PK (exchange, dir, feed_name, interval);
    //   does NOT touch discovered_first_month (phase-2 column) — omit it from both INSERT and UPDATE SET.
    // GetFeedStatuses: SELECT all columns except discovered_first_month WHERE exchange/dir; ORDER BY feed_name, interval.
    // ReplaceMonths: one transaction — DELETE WHERE (exchange,dir,feed_name,interval) then batch INSERT.
    // GetMonths: SELECT month, rows, file_len, file_mtime WHERE full key; ORDER BY month.
    // PruneFeedData: load existing (feed_name, interval) pairs for the asset; delete pairs not in keep
    //   from feed_status and month_partitions (computed in C#, deleted by PK — keep sets are small).
    // PruneAssetsNotIn: load all (exchange, dir); RemoveAsset each one absent from keep.
    // IsEmpty: SELECT NOT EXISTS(SELECT 1 FROM assets).
    // CreateJob: id = Guid.NewGuid().ToString("N"); state 'running'; created_at/updated_at = DateTime.UtcNow ISO ("O"). Returns id.
    // UpdateJob: UPDATE state, updated_at, COALESCE progress/error ($p IS NULL → keep old: use
    //   "progress_json = COALESCE($p, progress_json), error = COALESCE($err, error)").
    // GetJob: SELECT by id → IndexJobRow or null.
    // GetActiveJob: SELECT ... WHERE kind = $kind AND state = 'running' ORDER BY created_at DESC LIMIT 1.
    // GetLastJob: same as GetActiveJob without the state predicate.
    // ListFeedKeys: SELECT DISTINCT feed_name, interval FROM
    //   (SELECT feed_name, interval FROM feed_status WHERE exchange=$ex AND dir=$dir
    //    UNION SELECT feed_name, interval FROM month_partitions WHERE exchange=$ex AND dir=$dir).
}
```

Every stub comment above must be implemented as real code in this step — the comments here compress the plan, not the implementation. All reads use `ExecuteReaderAsync` with ordinal getters and `IsDBNull` checks for nullable columns.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~SqliteHistoryIndexTests"`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs
git commit -m "feat(index): IHistoryIndex repository over history-index.sqlite"
```

---

### Task 3: Extract pure coverage math — `MonthCoverageMath`

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Domain/MonthCoverageMath.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/MonthCoverageCalculator.cs` (delegate to the math)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/MonthCoverageMathTests.cs`

**Interfaces:**
- Consumes: `DataGap`, `IntervalParser`, `FeedNames` (all already in `AlgoTradeForge.HistoryLoader.Domain`).
- Produces:

```csharp
public static class MonthCoverageMath
{
    public static bool IsCovered(
        string feedName, string interval, int year, int month,
        long actualRows,
        IReadOnlyList<DataGap> gaps,
        IReadOnlyList<string>? completeMonths,
        long? effectiveStartMs,
        long nowMs);

    /// <summary>FirstTimestamp clamp: returns firstTs only when it falls inside (year, month).</summary>
    public static long? ListingClamp(long? firstTs, int year, int month);
}
```

**Why:** the index-backed coverage endpoint (Task 8) must compute the identical predicate from stored row counts. The current-month result depends on *now* — completeness cannot be precomputed at write time, so the math is evaluated at query time from indexed ingredients.

- [ ] **Step 1: Write the failing tests** — port the semantics, not the file I/O:

```csharp
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class MonthCoverageMathTests
{
    private static long Ms(int y, int m, int d = 1) =>
        new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    [Fact]
    public void FullPastMonth_ExactRowCount_IsCovered()
    {
        // Jan 2024, 1h → 744 expected rows.
        Assert.True(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 744,
            gaps: [], completeMonths: null, effectiveStartMs: null, nowMs: Ms(2025, 1)));
        Assert.False(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 743,
            gaps: [], completeMonths: null, effectiveStartMs: null, nowMs: Ms(2025, 1)));
    }

    [Fact]
    public void GapCredit_CoversMissingRows()
    {
        // 24h hole: gap ends are present rows → 23 creditable slots; 744 - 23 = 721 actual needed.
        var gaps = new[] { new DataGap { FromMs = Ms(2024, 1, 10), ToMs = Ms(2024, 1, 11) } };
        Assert.True(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 721,
            gaps, completeMonths: null, effectiveStartMs: null, nowMs: Ms(2025, 1)));
        Assert.False(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 720,
            gaps, completeMonths: null, effectiveStartMs: null, nowMs: Ms(2025, 1)));
    }

    [Fact]
    public void CurrentMonth_ExpectationClampedToNow()
    {
        // now = Jan 2 2024 00:00 → 24 expected 1h rows.
        Assert.True(MonthCoverageMath.IsCovered(
            FeedNames.Candles, "1h", 2024, 1, actualRows: 24,
            gaps: [], completeMonths: null, effectiveStartMs: null, nowMs: Ms(2024, 1, 2)));
    }

    [Fact]
    public void MonthlyCompletenessFeeds_UseMarkerOnly()
    {
        Assert.True(MonthCoverageMath.IsCovered(
            FeedNames.Ticks, "", 2024, 1, actualRows: 0,
            gaps: [], completeMonths: ["2024-01"], effectiveStartMs: null, nowMs: Ms(2025, 1)));
        Assert.False(MonthCoverageMath.IsCovered(
            FeedNames.Ticks, "", 2024, 1, actualRows: 999,
            gaps: [], completeMonths: [], effectiveStartMs: null, nowMs: Ms(2025, 1)));
    }

    [Fact]
    public void ListingClamp_OnlyInsideMonth()
    {
        Assert.Equal(Ms(2024, 1, 15), MonthCoverageMath.ListingClamp(Ms(2024, 1, 15), 2024, 1));
        Assert.Null(MonthCoverageMath.ListingClamp(Ms(2023, 12, 15), 2024, 1));
        Assert.Null(MonthCoverageMath.ListingClamp(null, 2024, 1));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail** (`--filter "FullyQualifiedName~MonthCoverageMathTests"`) — compile error.

- [ ] **Step 3: Implement** — move the body of `MonthCoverageCalculator.IsMonthCovered` (lines 27–70) verbatim into `MonthCoverageMath.IsCovered`, substituting `actualRows` for the file read and `nowMs` for `_clock.GetUtcNow()`. `ListingClamp` is the month-window check currently inlined at `CoverageEndpoints.cs:155-163`. Then reduce `MonthCoverageCalculator.IsMonthCovered` to: resolve the partition path, `CountDataRows` (unchanged memoized counting), and `return MonthCoverageMath.IsCovered(feedName, interval, year, month, actualRows, gaps, completeMonths, effectiveStartMs, _clock.GetUtcNow().ToUnixTimeMilliseconds())`.

- [ ] **Step 4: Run the new tests AND the calculator's existing tests**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~MonthCoverage"`
Expected: PASS — both the new math tests and every pre-existing `MonthCoverageCalculator` test (semantics unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Domain/MonthCoverageMath.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/MonthCoverageCalculator.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/MonthCoverageMathTests.cs
git commit -m "refactor(index): extract pure MonthCoverageMath from MonthCoverageCalculator"
```

---

### Task 4: `FeedMonthScanner` — per-feed partition scan with selective row counting

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/IFeedMonthScanner.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/FeedMonthScanner.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/FeedMonthScannerTests.cs`

**Interfaces:**
- Consumes: `MonthPartitionRow` (Task 2).
- Produces:

```csharp
public interface IFeedMonthScanner
{
    /// <summary>
    /// Enumerates {yyyy-MM}_{interval}.csv partitions in feedDir. Rows are recounted only when
    /// (file_len, file_mtime) differ from the known row — unchanged files reuse the known count.
    /// Interval-less feeds have no month partitions to scan; callers skip them.
    /// </summary>
    Task<IReadOnlyList<MonthPartitionRow>> Scan(
        string feedDir, string interval,
        IReadOnlyDictionary<string, MonthPartitionRow> known,
        CancellationToken ct = default);
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class FeedMonthScannerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-scan-").FullName;
    private readonly FeedMonthScanner _scanner = new();

    private string WriteCsv(string name, int dataRows)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, new[] { "ts,o,h,l,c,v" }
            .Concat(Enumerable.Range(0, dataRows).Select(i => $"{i},1,1,1,1,1")));
        return path;
    }

    [Fact]
    public async Task Scan_CountsRowsExcludingHeader()
    {
        WriteCsv("2024-01_1h.csv", 744);
        WriteCsv("2024-02_1h.csv", 696);
        WriteCsv("2024-01_1m.csv", 10);      // other interval — ignored
        File.WriteAllText(Path.Combine(_dir, "status_1h.json"), "{}"); // non-partition — ignored

        var rows = await _scanner.Scan(_dir, "1h", new Dictionary<string, MonthPartitionRow>());

        Assert.Equal(2, rows.Count);
        Assert.Equal(744, rows.Single(r => r.Month == "2024-01").Rows);
        Assert.Equal(696, rows.Single(r => r.Month == "2024-02").Rows);
    }

    [Fact]
    public async Task Scan_ReusesKnownCount_WhenLenAndMtimeMatch()
    {
        var path = WriteCsv("2024-01_1h.csv", 5);
        var fi = new FileInfo(path);
        var known = new Dictionary<string, MonthPartitionRow>
        {
            // Deliberately wrong count proves it was NOT recounted.
            ["2024-01"] = new("2024-01", 999, fi.Length, fi.LastWriteTimeUtc.ToString("O")),
        };

        var rows = await _scanner.Scan(_dir, "1h", known);
        Assert.Equal(999, Assert.Single(rows).Rows);
    }

    [Fact]
    public async Task Scan_RecountsChangedFile()
    {
        var path = WriteCsv("2024-01_1h.csv", 5);
        var known = new Dictionary<string, MonthPartitionRow>
        {
            ["2024-01"] = new("2024-01", 999, 1, DateTime.UnixEpoch.ToString("O")),
        };

        var rows = await _scanner.Scan(_dir, "1h", known);
        Assert.Equal(5, Assert.Single(rows).Rows);
    }

    [Fact]
    public async Task Scan_MissingDir_ReturnsEmpty()
    {
        Assert.Empty(await _scanner.Scan(Path.Combine(_dir, "nope"), "1h",
            new Dictionary<string, MonthPartitionRow>()));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
```

- [ ] **Step 2: Run to verify failure** (`--filter "FullyQualifiedName~FeedMonthScannerTests"`).

- [ ] **Step 3: Implement**

```csharp
using AlgoTradeForge.HistoryLoader.Application.Index;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

public sealed class FeedMonthScanner : IFeedMonthScanner
{
    public async Task<IReadOnlyList<MonthPartitionRow>> Scan(
        string feedDir, string interval,
        IReadOnlyDictionary<string, MonthPartitionRow> known,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(interval) || !Directory.Exists(feedDir))
            return [];

        var result = new List<MonthPartitionRow>();
        foreach (var file in Directory.EnumerateFiles(feedDir, $"????-??_{interval}.csv"))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            var underscore = name.IndexOf('_');
            if (underscore != 7) continue;                 // strict "yyyy-MM_" prefix
            var month = name[..underscore];
            if (!name[(underscore + 1)..].Equals(interval, StringComparison.Ordinal)) continue;

            var fi = new FileInfo(file);
            var mtime = fi.LastWriteTimeUtc.ToString("O");
            if (known.TryGetValue(month, out var k) && k.FileLen == fi.Length && k.FileMtimeUtc == mtime)
            {
                result.Add(k);
                continue;
            }
            result.Add(new MonthPartitionRow(month, await CountDataRows(file, ct), fi.Length, mtime));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Month, b.Month));
        return result;
    }

    private static async Task<long> CountDataRows(string path, CancellationToken ct)
    {
        long lines = 0;
        using var reader = new StreamReader(path);
        while (await reader.ReadLineAsync(ct) is not null) lines++;
        return Math.Max(0, lines - 1);
    }
}
```

- [ ] **Step 4: Run to verify pass** (`--filter "FullyQualifiedName~FeedMonthScannerTests"`).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Index/IFeedMonthScanner.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/FeedMonthScanner.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/FeedMonthScannerTests.cs
git commit -m "feat(index): FeedMonthScanner with len/mtime-gated row counting"
```

---

### Task 5: Maintenance pipeline — work queue, status-store decorator, work processor, hosted worker, DI

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/IndexWork.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/IIndexMaintenance.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/IndexMaintenanceQueue.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/AssetDirKey.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/ManifestJson.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/IndexingFeedStatusStore.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Index/IndexWorkProcessor.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Index/IndexMaintenanceService.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs` (index registrations + decorator swap at line 160)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (hosted service)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/IndexWorkProcessorTests.cs`, `tests/AlgoTradeForge.HistoryLoader.Tests/Index/IndexingFeedStatusStoreTests.cs`

**Interfaces:**
- Consumes: `IHistoryIndex`, `MonthPartitionRow`, `FeedStatusIndexRow`, `AssetIndexRow` (Task 2); `IFeedMonthScanner` (Task 4); `ISchemaManager`, `IFeedStatusStore`, `AssetDirectoryClassifier`, `FeedNames`, `FeedStatus`.
- Produces:

```csharp
public abstract record IndexWork
{
    public sealed record FeedTouched(string AssetDir, string FeedName, string Interval) : IndexWork;
    public sealed record ManifestTouched(string AssetDir) : IndexWork;
    public sealed record Rebuild(string JobId) : IndexWork;
}

public interface IIndexMaintenance { void Enqueue(IndexWork work); }

// IndexMaintenanceQueue : IIndexMaintenance — unbounded Channel<IndexWork>; exposes ChannelReader<IndexWork> Reader.

public static class AssetDirKey
{
    /// <summary>Splits an absolute asset dir into (exchange, dir) relative to dataRoot; null when outside dataRoot.</summary>
    public static (string Exchange, string Dir)? FromPath(string dataRoot, string assetDir);
}

public static class ManifestJson   // camelCase STJ options matching FeedSchemaManager's on-disk format
{
    public static readonly JsonSerializerOptions Options;
}

public sealed class IndexingFeedStatusStore(IFeedStatusStore inner, IIndexMaintenance maintenance) : IFeedStatusStore;

public sealed class IndexWorkProcessor(
    IHistoryIndex index, IFeedMonthScanner scanner, ISchemaManager schemaManager,
    IFeedStatusStore statusStore, IIndexRebuilder rebuilder,          // IIndexRebuilder lands in Task 6; stub here as internal no-op interface
    IOptionsMonitor<HistoryLoaderOptions> options, ILogger<IndexWorkProcessor> logger)
{
    public Task Process(IndexWork work, CancellationToken ct = default);
}
```

Note on ordering: `IIndexRebuilder` (one method `Task Run(string jobId, CancellationToken ct = default)`) is *declared* in this task (`Application/Index/IIndexRebuilder.cs`) so the processor compiles; its real implementation is Task 6. Add the interface file here with the single method.

- [ ] **Step 1: Write the failing tests**

`IndexingFeedStatusStoreTests.cs`:

```csharp
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using NSubstitute;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class IndexingFeedStatusStoreTests
{
    [Fact]
    public async Task Save_DelegatesThenEnqueuesFeedTouched()
    {
        var inner = Substitute.For<IFeedStatusStore>();
        var maintenance = Substitute.For<IIndexMaintenance>();
        var store = new IndexingFeedStatusStore(inner, maintenance);
        var status = new FeedStatus { FeedName = "candles", Interval = "1h" };

        await store.Save(@"C:\data\binance\BTCUSDT", "candles", "1h", status);

        await inner.Received(1).Save(@"C:\data\binance\BTCUSDT", "candles", "1h", status, Arg.Any<CancellationToken>());
        maintenance.Received(1).Enqueue(Arg.Is<IndexWork>(w =>
            w is IndexWork.FeedTouched f && f.FeedName == "candles" && f.Interval == "1h"));
    }

    [Fact]
    public async Task Load_DelegatesWithoutEnqueue()
    {
        var inner = Substitute.For<IFeedStatusStore>();
        var maintenance = Substitute.For<IIndexMaintenance>();
        var store = new IndexingFeedStatusStore(inner, maintenance);

        await store.Load("dir", "candles", "1h");

        maintenance.DidNotReceiveWithAnyArgs().Enqueue(default!);
    }
}
```

`IndexWorkProcessorTests.cs` — uses a real SQLite index + real scanner over a temp DataRoot, substitutes `ISchemaManager`/`IFeedStatusStore`:

```csharp
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class IndexWorkProcessorTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atf-proc-").FullName;
    private SqliteHistoryIndex _index = null!;
    private readonly ISchemaManager _schema = Substitute.For<ISchemaManager>();
    private readonly IFeedStatusStore _statusStore = Substitute.For<IFeedStatusStore>();
    private IndexWorkProcessor _processor = null!;

    public async Task InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_root, "idx.sqlite"));
        await init.EnsureCreated();
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = Path.Combine(_root, "data") });

        _processor = new IndexWorkProcessor(
            _index, new FeedMonthScanner(), _schema, _statusStore,
            Substitute.For<IIndexRebuilder>(), options, NullLogger<IndexWorkProcessor>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FeedTouched_UpsertsStatusAndMonths()
    {
        var assetDir = Path.Combine(_root, "data", "binance", "BTCUSDT");
        var feedDir = Path.Combine(assetDir, "candles");
        Directory.CreateDirectory(feedDir);
        File.WriteAllLines(Path.Combine(feedDir, "2024-01_1h.csv"),
            new[] { "ts,o,h,l,c,v" }.Concat(Enumerable.Range(0, 10).Select(i => $"{i},1,1,1,1,1")));
        _statusStore.Load(assetDir, "candles", "1h", Arg.Any<CancellationToken>())
            .Returns(new FeedStatus { FeedName = "candles", Interval = "1h", FirstTimestamp = 1, LastTimestamp = 2, RecordCount = 10 });

        await _processor.Process(new IndexWork.FeedTouched(assetDir, "candles", "1h"));

        var status = Assert.Single(await _index.GetFeedStatuses("binance", "BTCUSDT"));
        Assert.Equal(10, status.RecordCount);
        var month = Assert.Single(await _index.GetMonths("binance", "BTCUSDT", "candles", "1h"));
        Assert.Equal(("2024-01", 10L), (month.Month, month.Rows));
    }

    [Fact]
    public async Task ManifestTouched_UpsertsAssetRow_AndRemovesWhenManifestGone()
    {
        var assetDir = Path.Combine(_root, "data", "binance", "BTCUSDT_perp");
        _schema.Load(assetDir, Arg.Any<CancellationToken>())
            .Returns(new FeedMetadata());

        await _processor.Process(new IndexWork.ManifestTouched(assetDir));
        var row = await _index.GetAsset("binance", "BTCUSDT_perp");
        Assert.NotNull(row);
        Assert.Equal("BTCUSDT", row!.Symbol);   // AssetDirectoryClassifier strips _perp

        _schema.Load(assetDir, Arg.Any<CancellationToken>()).Returns((FeedMetadata?)null);
        await _processor.Process(new IndexWork.ManifestTouched(assetDir));
        Assert.Null(await _index.GetAsset("binance", "BTCUSDT_perp"));
    }

    [Fact]
    public async Task Rebuild_DelegatesToRebuilderWithJobId()
    {
        var rebuilder = Substitute.For<IIndexRebuilder>();
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _root });
        var processor = new IndexWorkProcessor(_index, new FeedMonthScanner(), _schema, _statusStore,
            rebuilder, options, NullLogger<IndexWorkProcessor>.Instance);

        await processor.Process(new IndexWork.Rebuild("job-1"));

        await rebuilder.Received(1).Run("job-1", Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Run to verify failure** (`--filter "FullyQualifiedName~IndexWorkProcessor|FullyQualifiedName~IndexingFeedStatusStore"`).

- [ ] **Step 3: Implement the Application pieces**

`IndexWork.cs`, `IIndexMaintenance.cs` — as in the Interfaces block. `IIndexRebuilder.cs`:

```csharp
namespace AlgoTradeForge.HistoryLoader.Application.Index;

public interface IIndexRebuilder
{
    Task Run(string jobId, CancellationToken ct = default);
}
```

`IndexMaintenanceQueue.cs`:

```csharp
using System.Threading.Channels;

namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed class IndexMaintenanceQueue : IIndexMaintenance
{
    private readonly Channel<IndexWork> _channel = Channel.CreateUnbounded<IndexWork>(
        new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<IndexWork> Reader => _channel.Reader;

    public void Enqueue(IndexWork work) => _channel.Writer.TryWrite(work);
}
```

`AssetDirKey.cs`:

```csharp
namespace AlgoTradeForge.HistoryLoader.Application.Index;

public static class AssetDirKey
{
    public static (string Exchange, string Dir)? FromPath(string dataRoot, string assetDir)
    {
        var rel = Path.GetRelativePath(Path.GetFullPath(dataRoot), Path.GetFullPath(assetDir));
        if (Path.IsPathRooted(rel)) return null;
        var segments = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || segments.Any(s => s == "..")) return null;
        return (segments[0], segments[1]);
    }
}
```

`ManifestJson.cs`:

```csharp
using System.Text.Json;

namespace AlgoTradeForge.HistoryLoader.Application.Index;

/// <summary>Matches FeedSchemaManager's on-disk camelCase so manifest_json round-trips FeedMetadata.</summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
```

`IndexingFeedStatusStore.cs`:

```csharp
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed class IndexingFeedStatusStore(IFeedStatusStore inner, IIndexMaintenance maintenance) : IFeedStatusStore
{
    public Task<FeedStatus?> Load(string assetDir, string feedName, string interval, CancellationToken ct = default) =>
        inner.Load(assetDir, feedName, interval, ct);

    public async Task Save(string assetDir, string feedName, string interval, FeedStatus status, CancellationToken ct = default)
    {
        await inner.Save(assetDir, feedName, interval, status, ct);
        maintenance.Enqueue(new IndexWork.FeedTouched(assetDir, feedName, interval));
    }
}
```

`IndexWorkProcessor.cs`:

```csharp
using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed class IndexWorkProcessor(
    IHistoryIndex index,
    IFeedMonthScanner scanner,
    ISchemaManager schemaManager,
    IFeedStatusStore statusStore,
    IIndexRebuilder rebuilder,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<IndexWorkProcessor> logger)
{
    public Task Process(IndexWork work, CancellationToken ct = default) => work switch
    {
        IndexWork.FeedTouched f => ProcessFeed(f, ct),
        IndexWork.ManifestTouched m => ProcessManifest(m, ct),
        IndexWork.Rebuild r => rebuilder.Run(r.JobId, ct),
        _ => Task.CompletedTask,
    };

    private async Task ProcessFeed(IndexWork.FeedTouched f, CancellationToken ct)
    {
        var key = AssetDirKey.FromPath(options.CurrentValue.DataRoot, f.AssetDir);
        if (key is null)
        {
            logger.LogWarning("Index: asset dir outside DataRoot, skipped: {AssetDir}", f.AssetDir);
            return;
        }
        var (exchange, dir) = key.Value;

        var status = await statusStore.Load(f.AssetDir, f.FeedName, f.Interval, ct);
        if (status is not null)
        {
            await index.UpsertFeedStatus(new FeedStatusIndexRow(
                exchange, dir, f.FeedName, f.Interval,
                status.FirstTimestamp, status.LastTimestamp, status.RecordCount,
                status.Health.ToString(),
                JsonSerializer.Serialize(status.Gaps, ManifestJson.Options),
                JsonSerializer.Serialize(status.CompleteMonths, ManifestJson.Options)), ct);
        }

        if (string.IsNullOrEmpty(f.Interval)) return;   // interval-less feeds: CompleteMonths only

        var known = (await index.GetMonths(exchange, dir, f.FeedName, f.Interval, ct))
            .ToDictionary(m => m.Month);
        var months = await scanner.Scan(Path.Combine(f.AssetDir, f.FeedName), f.Interval, known, ct);
        await index.ReplaceMonths(exchange, dir, f.FeedName, f.Interval, months, ct);
    }

    private async Task ProcessManifest(IndexWork.ManifestTouched m, CancellationToken ct)
    {
        var key = AssetDirKey.FromPath(options.CurrentValue.DataRoot, m.AssetDir);
        if (key is null) return;
        var (exchange, dir) = key.Value;

        var manifest = await schemaManager.Load(m.AssetDir, ct);
        if (manifest is null)
        {
            await index.RemoveAsset(exchange, dir, ct);
            return;
        }

        var (symbol, type) = AssetDirectoryClassifier.Classify(exchange, dir);
        await index.UpsertAsset(new AssetIndexRow(
            exchange, dir, symbol, type,
            JsonSerializer.Serialize(manifest, ManifestJson.Options)), ct);
    }
}
```

(If `AssetDirectoryClassifier.Classify`'s actual signature differs — it is used at `FeedCatalog.cs:166` as `var (symbol, type) = AssetDirectoryClassifier.Classify(exchangeName, dir);` — match that usage.)

- [ ] **Step 4: Implement the hosted worker**

`src/AlgoTradeForge.HistoryLoader.WebApi/Index/IndexMaintenanceService.cs`:

```csharp
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;

namespace AlgoTradeForge.HistoryLoader.WebApi.Index;

/// <summary>
/// Single consumer of the index work queue — serializes all SQLite writes. Subscribes to
/// ManifestChanged so manifest mutations index without polling; triggers an initial rebuild
/// when the index is empty (first boot or deleted DB).
/// </summary>
internal sealed class IndexMaintenanceService(
    IndexMaintenanceQueue queue,
    IndexWorkProcessor processor,
    HistoryIndexInitializer initializer,
    IHistoryIndex index,
    ISchemaManager schemaManager,
    ILogger<IndexMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await initializer.EnsureCreated(stoppingToken);

        schemaManager.ManifestChanged += assetDir =>
            queue.Enqueue(new IndexWork.ManifestTouched(assetDir));

        // Bootstrap: empty index (first boot / deleted DB) OR a rebuild that died mid-crawl.
        // IsEmpty alone is not enough — a 12k-asset crawl interrupted halfway leaves a non-empty
        // index with half the catalog silently missing (initializer marked that job 'interrupted').
        var lastRebuild = await index.GetLastJob("rebuild", stoppingToken);
        if (await index.IsEmpty(stoppingToken) || lastRebuild?.State == "interrupted")
        {
            var jobId = await index.CreateJob("rebuild", stoppingToken);
            queue.Enqueue(new IndexWork.Rebuild(jobId));
            logger.LogInformation("Index bootstrap rebuild queued as job {JobId} (empty={Empty}, lastState={LastState})",
                jobId, await index.IsEmpty(stoppingToken), lastRebuild?.State);
        }

        await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await processor.Process(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Index maintenance failed for {Work}", work);
            }
        }
    }
}
```

- [ ] **Step 5: Wire DI**

In `Infrastructure/DependencyInjection.cs`, replace line 160 (`services.AddSingleton<IFeedStatusStore, FeedStatusManager>();`) with:

```csharp
        services.AddSingleton<FeedStatusManager>();
        services.AddSingleton<IFeedStatusStore>(sp => new IndexingFeedStatusStore(
            sp.GetRequiredService<FeedStatusManager>(),
            sp.GetRequiredService<IIndexMaintenance>()));

        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<HistoryLoaderOptions>>().Value;
            return new HistoryIndexInitializer(HistoryIndexInitializer.ResolvePath(opts.Index));
        });
        services.AddSingleton<IHistoryIndex>(sp =>
            new SqliteHistoryIndex(sp.GetRequiredService<HistoryIndexInitializer>()));
        services.AddSingleton<IFeedMonthScanner, FeedMonthScanner>();
        services.AddSingleton<IndexMaintenanceQueue>();
        services.AddSingleton<IIndexMaintenance>(sp => sp.GetRequiredService<IndexMaintenanceQueue>());
        services.AddSingleton<IndexWorkProcessor>();
```

(Add usings: `AlgoTradeForge.HistoryLoader.Application.Index`, `AlgoTradeForge.HistoryLoader.Infrastructure.Index`. `IIndexRebuilder` registration comes in Task 6 — register a temporary `NullIndexRebuilder` here (`internal sealed class NullIndexRebuilder : IIndexRebuilder { public Task Run(string jobId, CancellationToken ct = default) => Task.CompletedTask; }` in `Infrastructure/Index/NullIndexRebuilder.cs`), replaced next task.)

In `Program.cs`, after `builder.Services.AddHostedService<StartupSweepService>();` add:

```csharp
builder.Services.AddHostedService<AlgoTradeForge.HistoryLoader.WebApi.Index.IndexMaintenanceService>();
```

- [ ] **Step 6: Build + run the new tests + full HistoryLoader suite**

Run: `dotnet build AlgoTradeForge.slnx`, then `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: build clean; all tests PASS (the decorator is behavior-transparent for existing collector tests — Save still writes status.json first).

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Index/ src/AlgoTradeForge.HistoryLoader.WebApi/Index/IndexMaintenanceService.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/NullIndexRebuilder.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/IndexWorkProcessorTests.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/IndexingFeedStatusStoreTests.cs
git commit -m "feat(index): incremental maintenance pipeline (status-store decorator + ManifestChanged + worker)"
```

---

### Task 6: Full rebuild as a job — `IndexRebuilder`, refresh endpoint → 202, job polling endpoint

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/IndexRebuilder.cs`
- Delete: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/NullIndexRebuilder.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs` (swap registration)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CatalogEndpoints.cs` (refresh → 202 + job endpoint)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Catalog/IFeedCatalog.cs` (drop `Refresh()`; keep the rest — check actual member list before editing)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/IndexRebuilderTests.cs`

**Interfaces:**
- Consumes: `IIndexRebuilder` (declared Task 5), `IHistoryIndex`, `IFeedMonthScanner`, `ISchemaManager`, `IFeedStatusStore`, `IFileStorage.ListKeys(prefix, suffix, recursive, ct)`, `FeedNames`, `AssetDirectoryClassifier`, `ManifestJson`.
- Produces: `IndexRebuilder : IIndexRebuilder`; `POST /api/v1/catalog/refresh` → `202 { job_id }` (returns the running job's id when one is already active); `GET /api/v1/index/jobs/{id}` → `200 { id, kind, state, progress, error }` or `404`.

- [ ] **Step 1: Write the failing test**

```csharp
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.State;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

public sealed class IndexRebuilderTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atf-rebuild-").FullName;
    private SqliteHistoryIndex _index = null!;
    private IndexRebuilder _rebuilder = null!;

    public async Task InitializeAsync()
    {
        var init = new HistoryIndexInitializer(Path.Combine(_root, "idx.sqlite"));
        await init.EnsureCreated();
        _index = new SqliteHistoryIndex(init, init.ConnectionString + ";Pooling=False");

        var dataRoot = Path.Combine(_root, "data");
        SeedAsset(dataRoot, "binance", "BTCUSDT", intervals: ["1h"], monthRows: ("2024-01", 744));

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = dataRoot });

        // Real components over the temp tree. LocalFileStorage + FeedSchemaManager +
        // FeedStatusManager are the production read path — construct them the same way
        // AddHistoryLoaderInfrastructure does (adjust ctor args to actual signatures).
        var storage = new LocalFileStorage();
        var schema = new FeedSchemaManager(storage);
        var statusStore = new FeedStatusManager(storage);

        _rebuilder = new IndexRebuilder(storage, options, schema, statusStore,
            new FeedMonthScanner(), _index, NullLogger<IndexRebuilder>.Instance);
    }

    private static void SeedAsset(string dataRoot, string exchange, string dir,
        string[] intervals, (string Month, int Rows) monthRows)
    {
        var assetDir = Path.Combine(dataRoot, exchange, dir);
        Directory.CreateDirectory(Path.Combine(assetDir, "candles"));
        File.WriteAllText(Path.Combine(assetDir, "feeds.json"),
            $$"""{"feeds":{},"candles":{"multiplier":100,"intervals":[{{string.Join(",", intervals.Select(i => $"\"{i}\""))}}]}}""");
        File.WriteAllLines(Path.Combine(assetDir, "candles", $"{monthRows.Month}_{intervals[0]}.csv"),
            new[] { "ts,o,h,l,c,v" }.Concat(Enumerable.Range(0, monthRows.Rows).Select(i => $"{i},1,1,1,1,1")));
        File.WriteAllText(Path.Combine(assetDir, "candles", $"status_{intervals[0]}.json"),
            """{"feedName":"candles","interval":"1h","firstTimestamp":1,"lastTimestamp":2,"recordCount":744,"gaps":[],"health":0,"completeMonths":[]}""");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Run_IndexesAssetsStatusesAndMonths_AndCompletesJob()
    {
        var jobId = await _index.CreateJob("rebuild");
        await _rebuilder.Run(jobId);

        var asset = Assert.Single(await _index.ListAssets());
        Assert.Equal(("binance", "BTCUSDT"), (asset.Exchange, asset.Dir));
        var month = Assert.Single(await _index.GetMonths("binance", "BTCUSDT", "candles", "1h"));
        Assert.Equal(744, month.Rows);
        Assert.Equal("completed", (await _index.GetJob(jobId))!.State);
    }

    [Fact]
    public async Task Run_PrunesRowsForAssetsGoneFromDisk()
    {
        await _index.UpsertAsset(new("binance", "GHOST", "GHOST", "Crypto", "{}"));
        var jobId = await _index.CreateJob("rebuild");

        await _rebuilder.Run(jobId);

        Assert.Null(await _index.GetAsset("binance", "GHOST"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
```

(Adjust `FeedSchemaManager`/`FeedStatusManager`/`LocalFileStorage` construction to their actual constructors; `FeedStatusManager` takes `IFileStorage`. The `health` field in seeded status JSON must match `FeedStatus`'s enum serialization — verify how `FeedStatusManager` round-trips `CollectionHealth` and seed accordingly, simplest is to write the file via `FeedStatusManager.Save` instead of a literal.)

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement `IndexRebuilder`**

```csharp
using System.Text.Json;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

public sealed class IndexRebuilder(
    IFileStorage storage,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ISchemaManager schemaManager,
    IFeedStatusStore statusStore,
    IFeedMonthScanner scanner,
    IHistoryIndex index,
    ILogger<IndexRebuilder> logger) : IIndexRebuilder
{
    public async Task Run(string jobId, CancellationToken ct = default)
    {
        try
        {
            var dataRoot = options.CurrentValue.DataRoot;
            var dirs = await ScanAssetDirs(dataRoot, ct);
            var keepAssets = new List<(string, string)>();

            for (var i = 0; i < dirs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (exchange, dir) = dirs[i];
                var assetDir = Path.Combine(dataRoot, exchange, dir);
                var manifest = await schemaManager.Load(assetDir, ct);
                if (manifest is null) continue;

                keepAssets.Add((exchange, dir));
                var (symbol, type) = AssetDirectoryClassifier.Classify(exchange, dir);
                await index.UpsertAsset(new AssetIndexRow(exchange, dir, symbol, type,
                    JsonSerializer.Serialize(manifest, ManifestJson.Options)), ct);

                var keepFeeds = new List<(string, string)>();
                foreach (var interval in manifest.Candles?.Intervals ?? [])
                {
                    keepFeeds.Add((FeedNames.Candles, interval));
                    // candle-ext is co-written per CANDLE interval, but manifest.Feeds holds a
                    // single entry whose Interval is just the last EnsureSchema write (verified:
                    // CandleFeedCollector.cs:45 / KlinesArchiveMaterializer.cs:144 call it per
                    // interval). Mirror candles' intervals — deriving from the manifest entry
                    // would index one interval while the incremental path indexes all of them,
                    // breaking the rebuild ≡ incremental invariant.
                    if (manifest.Feeds.ContainsKey(FeedNames.CandleExt))
                        keepFeeds.Add((FeedNames.CandleExt, interval));
                }
                foreach (var (feedName, def) in manifest.Feeds)
                {
                    if (feedName == FeedNames.CandleExt) continue;   // handled above, per candle interval
                    keepFeeds.Add((feedName, def.Interval ?? ""));
                }
                foreach (var feed in new[] { FeedNames.Ticks, FeedNames.FundingRate })
                    if (!keepFeeds.Any(k => k.Item1 == feed)) keepFeeds.Add((feed, ""));

                foreach (var (feedName, interval) in keepFeeds)
                {
                    var status = await statusStore.Load(assetDir, feedName, interval, ct);
                    if (status is not null)
                        await index.UpsertFeedStatus(new FeedStatusIndexRow(
                            exchange, dir, feedName, interval,
                            status.FirstTimestamp, status.LastTimestamp, status.RecordCount,
                            status.Health.ToString(),
                            JsonSerializer.Serialize(status.Gaps, ManifestJson.Options),
                            JsonSerializer.Serialize(status.CompleteMonths, ManifestJson.Options)), ct);

                    if (string.IsNullOrEmpty(interval)) continue;
                    var known = (await index.GetMonths(exchange, dir, feedName, interval, ct))
                        .ToDictionary(m => m.Month);
                    var months = await scanner.Scan(Path.Combine(assetDir, feedName), interval, known, ct);
                    await index.ReplaceMonths(exchange, dir, feedName, interval, months, ct);
                }
                await index.PruneFeedData(exchange, dir, keepFeeds, ct);

                if (i % 50 == 0)
                    await index.UpdateJob(jobId, "running",
                        progressJson: JsonSerializer.Serialize(new { assets_done = i + 1, assets_total = dirs.Count }), ct: ct);
            }

            await index.PruneAssetsNotIn(keepAssets, ct);
            await index.UpdateJob(jobId, "completed",
                progressJson: JsonSerializer.Serialize(new { assets_done = dirs.Count, assets_total = dirs.Count }), ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await index.UpdateJob(jobId, "interrupted", ct: CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Index rebuild {JobId} failed", jobId);
            await index.UpdateJob(jobId, "failed", error: ex.Message, ct: CancellationToken.None);
        }
    }

    // Same scan as FeedCatalog.ScanAssetDirs today (FeedCatalog.cs:124-149): ListKeys over
    // DataRoot with suffix feeds.json, recursive; trailing-separator guard included. Copy that
    // implementation here verbatim (FeedCatalog loses it in Task 7).
    private async Task<List<(string Exchange, string Dir)>> ScanAssetDirs(string dataRoot, CancellationToken ct) { /* copied body */ }
}
```

Write the copied `ScanAssetDirs` body in full. Note the `manifest.Feeds` interval handling: `FeedDefinition.Interval` may be null or empty — normalize to `""`.

- [ ] **Step 4: Swap DI + endpoints**

DI: replace the `NullIndexRebuilder` registration with `services.AddSingleton<IIndexRebuilder, IndexRebuilder>();` and delete `NullIndexRebuilder.cs`.

`CatalogEndpoints.cs` — replace the refresh mapping (lines 22-26) and add the job endpoint:

```csharp
        v1.MapPost("/catalog/refresh", async (IHistoryIndex index, IIndexMaintenance maintenance, CancellationToken ct) =>
        {
            // At 12k assets a rebuild is a long crawl — always a job, never synchronous (spec §3.3).
            var active = await index.GetActiveJob("rebuild", ct);
            if (active is not null)
                return Results.Accepted($"/api/v1/index/jobs/{active.Id}", new { job_id = active.Id });

            var jobId = await index.CreateJob("rebuild", ct);
            maintenance.Enqueue(new IndexWork.Rebuild(jobId));
            return Results.Accepted($"/api/v1/index/jobs/{jobId}", new { job_id = jobId });
        });

        v1.MapGet("/index/jobs/{id}", async (string id, IHistoryIndex index, CancellationToken ct) =>
        {
            var job = await index.GetJob(id, ct);
            return job is null
                ? Results.NotFound(new { error = "job not found", id })
                : Results.Ok(new { id = job.Id, kind = job.Kind, state = job.State,
                    progress = System.Text.Json.JsonDocument.Parse(job.ProgressJson).RootElement,
                    error = job.Error });
        });
```

Remove `Refresh()` from `IFeedCatalog` and from `FeedCatalog` (grep `catalog.Refresh` / `\.Refresh\(` across `src/` and `tests/` — the endpoint above was its only production caller; fix any test callers in the same change).

- [ ] **Step 5: Run tests** — `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` (rebuilder tests + everything else green; update any endpoint tests pinning the old 204 refresh contract in this task).

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/IndexRebuilder.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CatalogEndpoints.cs src/AlgoTradeForge.HistoryLoader.Application/Catalog/IFeedCatalog.cs src/AlgoTradeForge.HistoryLoader.Application/Catalog/FeedCatalog.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/IndexRebuilderTests.cs
git rm src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/NullIndexRebuilder.cs
git commit -m "feat(index): full rebuild as tracked job; POST /catalog/refresh -> 202 {job_id}"
```

---

### Task 7: Catalog list endpoints read from the index

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Catalog/FeedCatalog.cs` (rewrite internals)
- Test: update existing catalog tests + `tests/AlgoTradeForge.HistoryLoader.Tests/Index/FeedCatalogIndexTests.cs`

**Interfaces:**
- Consumes: `IHistoryIndex.ListAssets/GetAsset`, `ManifestJson.Options`, `FeedMetadata`.
- Produces: `FeedCatalog(IHistoryIndex index, IOptionsMonitor<HistoryLoaderOptions> options, ISchemaManager schemaManager)` — same `IFeedCatalog` surface minus `Refresh`. **Response shapes unchanged.**

Behavior mapping (keep `MapFeed`, `FeedOrder`, `TryResolveAssetDir`, `GetFeed` exactly as they are):
- `GetExchanges` → `ListAssets()` grouped by `Exchange` (OrdinalIgnoreCase), counts, ordered by name — replaces the `ScanAssetDirs` walk.
- `GetAssetsByExchange(ex)` / `GetAllAssets()` → `ListAssets(ex)` rows; per row deserialize `ManifestJson` → `FeedMetadata` (`JsonSerializer.Deserialize<FeedMetadata>(row.ManifestJson, ManifestJson.Options)`), then the existing candle-interval + declared-feed mapping (current `BuildAssetEntries` body from the manifest onward, using `row.Symbol`/`row.Type` instead of re-classifying).
- `GetAsset(ex, symbol)` → `index.GetAsset(ex, symbol)` (symbol == dir name, as today) → build one entry.
- Delete: `IMemoryCache` usage, `CachedAsync`, `_loadGates`, `_version`, `ManifestChanged` subscription, `ScanAssetDirs`, `Refresh` — SELECTs need no cache.
- `GetFeed` keeps reading via `ISchemaManager.Load` (targeted single-file read, no crawl): aggregation flows call it immediately after manifest mutation and must not race the async index queue.

**Consistency note (document in code where the ctor loses ManifestChanged):** catalog lists are now eventually consistent behind the maintenance queue (single-digit ms typical). The FE already refetches on job completion; acceptable per spec §3.3.

- [ ] **Step 1: Write failing tests** — `FeedCatalogIndexTests`: seed a real `SqliteHistoryIndex` (temp DB) with 2 assets across 2 exchanges whose `manifest_json` declares one candle interval + one alt-bar feed; assert `GetExchanges` counts, `GetAllAssets` returns feeds ordered per `FeedOrder` (time bar before alt bar), and `GetAsset` round-trips. Construct `FeedCatalog` with substitutes for options/schema manager.
- [ ] **Step 2: Run — fails** (ctor signature + behavior).
- [ ] **Step 3: Rewrite `FeedCatalog`** per mapping above.
- [ ] **Step 4: Update existing catalog tests** that constructed `FeedCatalog` with `IFileStorage`/`IMemoryCache` — convert their fixtures to seed the index instead (same assertions). Run `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` — all green.
- [ ] **Step 5: Commit** (`FeedCatalog.cs`, changed test files) — `"feat(index): catalog lists served from history index (crawl removed from hot path)"`.

---

### Task 8: Coverage endpoint reads from the index

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CoverageEndpoints.cs`
- Test: update existing coverage endpoint tests; add index-seeded cases.

**Interfaces:**
- Consumes: `IHistoryIndex.GetAsset/GetFeedStatuses/GetMonths`, `MonthCoverageMath.IsCovered/ListingClamp` (Task 3), `ManifestJson.Options`, `TimeProvider`.
- Produces: `GetCoverage(string exchange, string symbol, string assetType, IOptionsMonitor<HistoryLoaderOptions> options, IHistoryIndex index, TimeProvider clock, CancellationToken ct)` — **wire shape identical** (`asset_dir`, `feeds[]` with `feed_name`, `interval`, `covered_months`, `first_timestamp`, `last_timestamp`).

Rewrite of `GetCoverage` internals (validation block unchanged):
1. `dir = AssetPathConvention.DirectoryName(symbol, assetType)`; `assetDir` string built exactly as today for the response.
2. `asset = await index.GetAsset(exchange, dir, ct)`; null → same empty response as a missing manifest today.
3. `manifest = JsonSerializer.Deserialize<FeedMetadata>(asset.ManifestJson, ManifestJson.Options)`.
4. `statuses = await index.GetFeedStatuses(exchange, dir, ct)` into a dictionary keyed `(FeedName, Interval)`; gaps/completeMonths deserialized from the JSON columns with `ManifestJson.Options`.
5. **Presence rule (load-bearing for the 12k equity catalog, D6):** an interval'd feed is present when it has a status row **OR** month rows — never require both. Static equity assets have month partitions but typically no `status_*.json`; today's `BuildFeedEntry` handles `status == null` gracefully (`gaps = status?.Gaps ?? []`, no clamp), and the index path must preserve that: missing status → `gaps = []`, `completeMonths = null`, `first_timestamp`/`last_timestamp` = null, no listing clamp.
6. Candle intervals + declared interval feeds: `covered_months` = `GetMonths(...)` filtered through `MonthCoverageMath.IsCovered(feedName, interval, year, month, row.Rows, gaps, completeMonths, MonthCoverageMath.ListingClamp(status?.FirstTs, year, month), clock.GetUtcNow().ToUnixTimeMilliseconds())` — month parsed from `row.Month` (`yyyy-MM`), `status` nullable per the presence rule.
7. `candle-ext` shadow: unchanged logic, but presence = status row **or** month rows exist for (`candle-ext`, interval) instead of `Directory.Exists`; mirrors candles' covered months per interval.
8. Interval-less `ticks`/`funding-rate`: presence = status row exists (unchanged — their coverage IS the status marker); `covered_months` = sorted `CompleteMonths` from the status row.

No `ISchemaManager`, no `IFeedStatusStore`, no `IMonthCoverageCalculator`, no `Directory.*` in this endpoint afterwards. (`IMonthCoverageCalculator` stays in DI — the backfill planner still uses it.)

- [ ] **Step 1: Write failing tests** — endpoint-level (it's `internal static`, tests call `CoverageEndpoints.GetCoverage` directly): seed index with a candles/1h status (one gap) + month rows where one month is complete and one is short; assert `covered_months` contains exactly the complete month; assert ticks entry appears with `CompleteMonths` and candle-ext mirrors candles. Match the new parameter list. **Must include the status-less fixture:** an equity-shaped asset with month rows and NO feed_status rows — assert its feed entry is present, `covered_months` computed from row counts alone, `first_timestamp`/`last_timestamp` null.
- [ ] **Step 2: Run — fails.**
- [ ] **Step 3: Implement the rewrite.**
- [ ] **Step 4: Update pre-existing coverage tests** (they seed CSV files + status.json on disk today): re-point their arrange phase at the index (either direct `IHistoryIndex` upserts, or by running `IndexWorkProcessor` over their existing on-disk fixtures — prefer the latter where the test intent is end-to-end). Run full suite: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/` — green.
- [ ] **Step 5: Commit** — `"feat(index): coverage endpoint served from history index (per-request CSV row-count crawl removed)"`.

---

### Task 9: Drift sweep, rebuild≡incremental invariant, proxy/frontend verification, full regression

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Index/DriftSweepService.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/IndexEquivalenceTests.cs`
- Verify/adjust: `src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs:56` (refresh proxy), `tests/AlgoTradeForge.WebApi.Tests/Data/DataProxyTests.cs`, `frontend/lib/services/data-api.ts`

**Interfaces:**
- Consumes: everything above.
- Produces: `DriftSweepService : BackgroundService` — every `IndexOptions.DriftSweepMinutes` (default 60): for each indexed asset × interval'd feed, `Directory.EnumerateFiles` + `FileInfo` stat only; enqueue `IndexWork.FeedTouched` when the on-disk (len, mtime, month-set) disagrees with `GetMonths`. No file content reads in the sweep itself.

- [ ] **Step 1: Write the invariant test (the spec's §5 core)**

```csharp
// IndexEquivalenceTests: build a temp DataRoot fixture, then:
//   incremental = fresh DB; for each asset: Process(ManifestTouched), then per feed Process(FeedTouched)
//   rebuilt     = second fresh DB; CreateJob + IndexRebuilder.Run
// Snapshot all four tables from both DBs (ordered SELECTs, skipping volatile indexed_at /
// job rows) and assert row-set equality. This is the "full rebuild scan ≡ incrementally
// maintained index" invariant from the spec.
```

The invariant only proves equivalence for the data shapes the fixture contains — its value is fixture diversity, so the fixture MUST include the shapes where the two code paths structurally diverge:

1. an asset with candles on **two intervals** and a gap in one status;
2. the same asset with **candle-ext on both candle intervals** (single manifest entry, per-interval status files — the shape that breaks manifest-derived keepFeeds);
3. an equity-shaped asset with **month partitions and no status files** (drive its incremental side with explicit `FeedTouched` per feed — no writers exist for it in production, rebuild and drift sweep are its only sources);
4. `ticks` with `CompleteMonths` and no month partitions.

Write it in full: reuse the fixture helpers from `IndexRebuilderTests` (extract shared seeding into `tests/.../Index/IndexFixture.cs` if duplication exceeds ~30 lines). Snapshot helper: query each table `SELECT * ... ORDER BY <pk>` into `List<string>` of joined column values; `Assert.Equal(snapA, snapB)` per table.

- [ ] **Step 2: Run — fails** (only because `DriftSweepService`/fixture don't exist yet; the equivalence itself should pass once written — if it does not, the divergence is a bug in Tasks 5/6 to fix now, not to accommodate).

- [ ] **Step 3: Implement `DriftSweepService`**

```csharp
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Index;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Index;

/// <summary>
/// Cheap periodic reconciliation of index vs disk (spec §3.3): stat-only comparison, re-scan
/// enqueued just for mismatched feeds. Catches manual file edits without a full rebuild.
/// </summary>
internal sealed class DriftSweepService(
    IHistoryIndex index,
    IIndexMaintenance maintenance,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<DriftSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMinutes(Math.Max(1, options.CurrentValue.Index.DriftSweepMinutes));
        using var timer = new PeriodicTimer(period);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await Sweep(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Drift sweep failed");
            }
        }
    }

    private async Task Sweep(CancellationToken ct)
    {
        var dataRoot = options.CurrentValue.DataRoot;
        foreach (var asset in await index.ListAssets(ct: ct))
        {
            ct.ThrowIfCancellationRequested();
            var assetDir = Path.Combine(dataRoot, asset.Exchange, asset.Dir);
            // ListFeedKeys, not GetFeedStatuses: feeds indexed only as month rows (static
            // equity, no status_*.json) must still be swept for drift.
            foreach (var (feedName, interval) in await index.ListFeedKeys(asset.Exchange, asset.Dir, ct))
            {
                if (string.IsNullOrEmpty(interval)) continue;
                var known = await index.GetMonths(asset.Exchange, asset.Dir, feedName, interval, ct);
                if (HasDrift(Path.Combine(assetDir, feedName), interval, known))
                    maintenance.Enqueue(new IndexWork.FeedTouched(assetDir, feedName, interval));
            }
        }
    }

    private static bool HasDrift(string feedDir, string interval, IReadOnlyList<MonthPartitionRow> known)
    {
        var onDisk = new Dictionary<string, (long Len, string Mtime)>();
        if (Directory.Exists(feedDir))
            foreach (var file in Directory.EnumerateFiles(feedDir, $"????-??_{interval}.csv"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var underscore = name.IndexOf('_');
                if (underscore != 7) continue;
                var fi = new FileInfo(file);
                onDisk[name[..underscore]] = (fi.Length, fi.LastWriteTimeUtc.ToString("O"));
            }

        if (onDisk.Count != known.Count) return true;
        foreach (var k in known)
            if (!onDisk.TryGetValue(k.Month, out var d) || d.Len != k.FileLen || d.Mtime != k.FileMtimeUtc)
                return true;
        return false;
    }
}
```

Register in `Program.cs` next to `IndexMaintenanceService`:

```csharp
builder.Services.AddHostedService<AlgoTradeForge.HistoryLoader.WebApi.Index.DriftSweepService>();
```

- [ ] **Step 4: Verify the proxy and frontend against the new 202 contract**

- Read `src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs:40-70`: the refresh proxy must forward upstream status + body (202 + `{job_id}`) instead of assuming 204. Adjust if it synthesizes its own status. Update `DataProxyTests` assertions pinning 204 accordingly.
- `Grep "refresh" frontend/lib/services/data-api.ts frontend/components/features/data/`: if the FE call parses the response, widen its type to `{ job_id?: string }`; if it ignores the body (fire-and-forget + query invalidation), no change. Run `npm run lint` and `npx tsc --noEmit` in `frontend/` if touched.

- [ ] **Step 5: Full regression, sequentially**

```
dotnet build AlgoTradeForge.slnx
dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/
dotnet test tests/AlgoTradeForge.Domain.Tests/
dotnet test tests/AlgoTradeForge.Application.Tests/
dotnet test tests/AlgoTradeForge.Infrastructure.Tests/
dotnet test tests/AlgoTradeForge.WebApi.Tests/
dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx
```

Expected: all green, no warnings introduced. (One at a time — never in parallel.)

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Index/DriftSweepService.cs src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/IndexEquivalenceTests.cs
# plus DataEndpoints.cs / DataProxyTests.cs / data-api.ts if step 4 touched them — list explicitly
git commit -m "feat(index): drift sweep + rebuild/incremental equivalence invariant; refresh proxy 202"
```

---

## Post-plan verification (manual, after all tasks)

1. Start HistoryLoader against the real `HistoryTest` root (VS Code launch settings, port 5210 service must NOT be running the old build): first boot logs "Index empty — initial rebuild queued", `GET /api/v1/index/jobs/{id}` shows progress → `completed`.
2. `GET /api/v1/assets` and `/api/v1/coverage?...` respond in milliseconds on repeat calls; compare payloads against a pre-branch capture of the same endpoints on the same data root (shapes must match byte-for-byte modulo ordering guarantees already asserted).
3. Trigger one aggregation from the UI → new alt-bar feed appears in the catalog after job completion (ManifestChanged → index path).
