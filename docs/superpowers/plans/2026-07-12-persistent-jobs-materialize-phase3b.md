# Persistent Jobs + Materialize + Frontend (Phase 3b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace both in-memory job registries with a SQLite-backed unified job store, add a `materialize` composite job, and give the frontend one durable Jobs panel — so job state survives restarts and derived feeds materialize in one click.

**Architecture:** `index_jobs` (phase-1 table) generalizes into THE durable store (`kind ∈ load|aggregation|materialize|index`) with a child `job_events` table. All index writes serialize behind one process-wide gate; the feed busy-gate is an atomic claim backed by a UNIQUE partial index. Live SSE liveness comes from a content-free in-process doorbell over the durable `job_events` tail. Load/aggregation work is extracted into callable services invoked by both standalone job kinds and the materialize composite. The frontend collapses its two localStorage job stores into one server-hydrated Jobs panel with uniform SSE.

**Tech Stack:** C# 14 / .NET 10, `Microsoft.Data.Sqlite`, `Microsoft.Extensions.Hosting` (BackgroundService), `System.Threading.Channels`, ASP.NET Core minimal APIs, Serilog; frontend Next.js 16 + TypeScript 5 strict + TanStack Query + `@microsoft/fetch-event-source`.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-07-12-persistent-jobs-materialize-phase3b-design.md`. Every task implicitly inherits it.
- **One `dotnet` process at a time.** Build `dotnet build AlgoTradeForge.slnx`; test `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`. Never run build/test/run in parallel.
- **Shell:** PowerShell 5.1 (no `pwsh`, no `&&` — use `; if ($?) { }`). Bash tool available for POSIX.
- **Int64 money convention, async-no-`Async`-suffix, `using` over try/finally, one-type-per-file (partials allowed), comment-terse** — all per `CLAUDE.md`. New async I/O methods take `CancellationToken ct = default` and drop the `Async` suffix.
- **SQLite tests:** `Pooling=False` in the connection string + `SqliteConnection.ClearAllPools()` where a temp file must be deleted; `TestContext.Current.CancellationToken` for the ct; xUnit `IAsyncLifetime`.
- **No auto-staging** — never `git add` on the human's behalf beyond the exact files a task's commit names. Commit trailers (verbatim last two lines of every commit):
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`
  `Claude-Session: https://claude.ai/code/session_01FS9nV12EvhcnTbE3CpUsvc`
- **Branch:** `feat/persistent-jobs-phase3b`, cut off fresh `main` at execution start. The spec + this plan ride as the first branch commit; task commits touch code/tests only (nothing under `docs/superpowers/**`).
- **API bodies are snake_case** (`asset_type`, `feed_name`, `job_id`).
- **Post-merge:** restart the standalone HistoryLoader service on port **:5210**.

---

## Shared Contracts

These names are fixed here and referenced verbatim by every task. Do not rename at a seam.

### Job vocabulary

- `JobKind` (string discriminator, stored lowercase): `"load" | "aggregation" | "materialize" | "index"`.
- `JobState` (string, stored lowercase): `"queued" | "running" | "complete" | "error" | "cancelled" | "interrupted"`.
- **feed_key grammar (one canonical form for every kind):** `{exchange}|{assetDir}|{feedName}|{interval}` (interval `""` for interval-less feeds). **The exchange is the first segment** so two venues sharing an assetDir name (e.g. `binance` + `bybit` both with `BTCUSDT_perp`) never falsely contend on one gate, and the interrupted-sweep can recover `(exchange, dir)` for `month_partitions` lookups (§S9). A load/collection keys on its source feed; an aggregation and a materialize key on the **output** derived feed. `null` for `index` jobs. The old `LoadEndpoints` 3-part `{assetDir}|{feedName}|{interval}` grammar is replaced everywhere by this 4-part form.
- **Every gated job persists a `request_json`** — the serialized creation request the worker rehydrates to rebuild its typed work item after a restart (a bare `feed_key` cannot reconstruct `From/To/asset` for a load or the whole `AggregationJob` for an aggregation). Without it a boot-seeded job can never run and permanently holds its feed-gate.

### `IHistoryIndex` job additions (Application/Index/IHistoryIndex.cs)

Records (declared beside the interface):

```csharp
public sealed record IndexJobRow(
    string Id, string Kind, string State, string ProgressJson, string? Error,
    string? FeedKey, bool CancelRequested, string TouchedJson, string? RequestJson);
    // TouchedJson = "[]" or [{"feedKey":..,"month":..}]; RequestJson = serialized creation request (null for index jobs)

public sealed record JobEventRow(int Seq, string Kind, string PayloadJson, string CreatedAtUtc);

public sealed record InterruptedJobRow(string Id, string Kind, string? FeedKey, string TouchedJson);

public abstract record FeedGateOutcome
{
    public sealed record Acquired(string JobId) : FeedGateOutcome;
    public sealed record Busy(string ExistingJobId) : FeedGateOutcome;
}
```

New interface methods (all `CancellationToken ct = default`):

```csharp
// Atomic create-and-claim for gated kinds (load/aggregation/materialize). requestJson is persisted for rehydration.
Task<FeedGateOutcome> TryAcquireFeedGate(string kind, string feedKey, string progressJson, string requestJson, ct);
// Gateless create stays as-is for index/catalog jobs (feed_key NULL, request_json NULL, state 'queued').
Task<string> CreateJob(string kind, ct);                       // existing signature; now inserts state='queued'
Task UpdateJob(string id, string state, string? progressJson = null, string? error = null, ct);  // existing
Task<IndexJobRow?> GetJob(string id, ct);                      // existing shape, extended row (incl. RequestJson)
Task<IReadOnlyList<IndexJobRow>> ListJobs(string? kind, string? state, ct);
Task<int> AppendJobEvent(string jobId, string eventKind, string payloadJson, ct);   // returns assigned seq; trims progress events to JobsOptions.MaxEventsPerJob
Task<IReadOnlyList<JobEventRow>> GetJobEventsAfter(string jobId, int afterSeq, ct);
Task<int> GetLastEventSeq(string jobId, ct);
Task RequestCancel(string jobId, ct);                          // sets cancel_requested=1
Task SetTouched(string jobId, string feedKey, string month, ct);   // single in-flight (feedKey,month), written BEFORE fetch
Task<IReadOnlyList<InterruptedJobRow>> ListInterruptedJobs(ct);
Task DeleteJob(string jobId, ct);                              // deletes one row + its events (QueueFull rollback; client never saw the job)
Task<int> DeleteTerminalJobsBefore(DateTimeOffset cutoffUtc, ct);  // deletes rows + their job_events; returns count
```

### `IJobEventSignal` (Application/Jobs/IJobEventSignal.cs) — the content-free doorbell

```csharp
public interface IJobEventSignal
{
    Task Next(string jobId);   // awaitable that completes on the next Signal(jobId); lazily creates the cell
    void Signal(string jobId); // swaps in a fresh TCS and completes the previous
    void Evict(string jobId);  // drop the per-job cell on terminal
}
```

### `IJobProgressSink` (Application/Jobs/IJobProgressSink.cs) — the single write seam onto the store

```csharp
public interface IJobProgressSink
{
    Task Report(string progressJson, ct);              // UpdateJob(progress) + AppendJobEvent("progress") + Signal
    Task Started(string startedPayloadJson, ct);       // UpdateJob(state='running') + AppendJobEvent("started") + Signal
    Task Complete(string resultPayloadJson, ct);       // UpdateJob('complete') + AppendJobEvent("complete") + Signal + Evict
    Task Fail(string code, string message, ct);        // UpdateJob('error', err) + AppendJobEvent("error") + Signal + Evict
    Task Cancel(string reason, ct);                    // UpdateJob('cancelled') + AppendJobEvent("cancelled") + Signal + Evict
}
```
A `JobProgressSink` is constructed per job with its `jobId` bound; `MaterializeProgressSink` wraps a base sink and rewrites stage progress into the composite envelope (M4).

### Extracted work services (Application/Jobs/)

```csharp
public sealed record ArchiveLoadRequest(CollectionAsset Asset, string FeedName, string Interval, DateOnly From, DateOnly To);
public interface IArchiveLoadService { Task<bool> Run(ArchiveLoadRequest req, IJobProgressSink sink, ct); }

public sealed record AggregationRunRequest(AggregationJob Job);
public interface IAggregationService { Task Run(AggregationRunRequest req, IJobProgressSink sink, ct); }
```

### Unified job wire envelope (returned by `GET /jobs`, `GET /jobs/{id}`, and SSE `progress` data)

```jsonc
{
  "job_id": "…", "kind": "load|aggregation|materialize|index",
  "state": "queued|running|complete|error|cancelled|interrupted",
  "feed_key": "…|…|…",          // null for index
  "created_at": "…", "updated_at": "…",
  "error": { "code": "…", "message": "…" } | null,
  "progress": { "phase": "…", "done": 0, "total": 0, "detail": { /* kind-specific */ } }
}
```

### Config (Application/HistoryLoaderOptions.cs)

```csharp
public sealed class JobsOptions
{
    public int RetentionMinutes { get; init; } = 30;   // mirrors today's Load.JobRetentionMinutes
    public int RetentionSweepMinutes { get; init; } = 5;
    public int MaxEventsPerJob { get; init; } = 500;   // progress-event cap per job
    public int WakeupChannelDepth { get; init; } = 64; // per-host dispatch channel bound (QueueFull backpressure)
}
// HistoryLoaderOptions gains: public JobsOptions Jobs { get; init; } = new();
```

---

## Milestone M1 — Durable job store foundation

Extends `index_jobs` into the unified store with a real migration, a write gate, atomic feed-gate, and the `job_events` child table. All contract-tested against `SqliteHistoryIndexTests` patterns. No behavior change to callers yet — this milestone is independently mergeable and testable.

### Task M1.1: Version-guarded schema migration

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/HistoryIndexInitializer.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/HistoryIndexInitializerTests.cs`

**Interfaces:**
- Produces: `history-index.sqlite` at `schema_version = 2` with `index_jobs.feed_key/cancel_requested/touched_json`, table `job_events`, indexes `ix_jobs_kind_state` + `ux_jobs_active_feedkey`. Idempotent across repeated `EnsureCreated`.

- [ ] **Step 1: Write the failing test** — migrating a v1 DB twice must not throw.

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~EnsureCreated_OnV1Db_MigratesToV2`
Expected: FAIL — either `schema_version` stays 1 or a "duplicate column name" throw on the second `EnsureCreated`.

- [ ] **Step 3: Implement the guarded migration**

In `HistoryIndexInitializer`: bump `CurrentVersion` to `2`. Keep the base `CREATE TABLE IF NOT EXISTS` block (unchanged, so a brand-new DB is born at v2 directly — the migration ALTERs are a no-op path guarded by the stored version). After the base schema runs and the version row is ensured, read the stored version and run the delta once:

```csharp
private const int CurrentVersion = 2;

// After schemaCmd + versionCmd (insert-if-absent), before the startup sweep:
await using (var readVer = conn.CreateCommand())
{
    readVer.CommandText = "SELECT version FROM schema_version LIMIT 1";
    var stored = Convert.ToInt32(await readVer.ExecuteScalarAsync(ct));
    if (stored < 2)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var mig = conn.CreateCommand();
        mig.Transaction = (SqliteTransaction)tx;
        // ALTER TABLE ADD COLUMN has no IF NOT EXISTS — guarded by stored<2 so it runs exactly once.
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

// AFTER the migration block (NOT in the base schema blob) — unconditional + idempotent, so the
// columns are guaranteed present whether this is a fresh DB (base CREATE included them) or a
// migrated v1 DB (ALTER just added them). Putting ux_jobs_active_feedkey in the base blob would
// run against a v1 index_jobs that has no feed_key column yet → "no such column: feed_key" (B1).
await using (var idx = conn.CreateCommand())
{
    idx.CommandText = """
        CREATE INDEX IF NOT EXISTS ix_jobs_kind_state ON index_jobs(kind, state);
        CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active_feedkey
            ON index_jobs(feed_key) WHERE feed_key IS NOT NULL AND state IN ('queued','running');
        """;
    await idx.ExecuteNonQueryAsync(ct);
}
```

Also: (a) update the base `Schema` const's `CREATE TABLE index_jobs (...)` to include the four new columns (`feed_key TEXT NULL, cancel_requested INTEGER NOT NULL DEFAULT 0, touched_json TEXT NOT NULL DEFAULT '[]', request_json TEXT NULL`) so a brand-new DB is born complete; (b) append the `job_events` `CREATE TABLE IF NOT EXISTS` (below) to the base blob — it references no new column, so it is safe in the base blob:

```sql
CREATE TABLE IF NOT EXISTS job_events (
    job_id       TEXT    NOT NULL,
    seq          INTEGER NOT NULL,
    kind         TEXT    NOT NULL,
    payload_json TEXT    NOT NULL,
    created_at   TEXT    NOT NULL,
    PRIMARY KEY (job_id, seq)
);
```

Keep the startup sweep (`UPDATE index_jobs SET state='interrupted' WHERE state='running'`) **after** the migration + index block.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~EnsureCreated_OnV1Db_MigratesToV2`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/HistoryIndexInitializer.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/HistoryIndexInitializerTests.cs
git commit -m "feat(index): version-guarded migration of index_jobs to v2 (feed_key, cancel_requested, touched_json, job_events)"
```

### Task M1.2: Process-wide index write gate + busy_timeout + `queued` births

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs`

**Interfaces:**
- Produces: every *write* op on `SqliteHistoryIndex` serializes behind one `SemaphoreSlim(1,1)`; `Open` sets `PRAGMA busy_timeout=5000`; `CreateJob` inserts `state='queued'`.

- [ ] **Step 1: Write the failing test** — many concurrent job-event appends allocate strictly monotonic seqs with no `SQLITE_BUSY`.

```csharp
[Fact]
public async Task AppendJobEvent_ConcurrentAppends_MonotonicSeq_NoBusy()
{
    var jobId = await _index.CreateJob("load", Ct);
    var tasks = Enumerable.Range(0, 50)
        .Select(i => _index.AppendJobEvent(jobId, "progress", $$"""{"i":{{i}}}""", Ct));
    var seqs = await Task.WhenAll(tasks);   // must not throw SqliteException(SQLITE_BUSY)
    Assert.Equal(Enumerable.Range(1, 50), seqs.OrderBy(s => s));   // 1..50, all distinct
}
```

This is a **green contract test** — it goes red only because `AppendJobEvent` does not exist yet (compile error until M1.3), then green. Its invariant (N concurrent appends → exactly the distinct seqs `1..N`) is what the write gate guarantees; it is not a flaky "expect a throw" test, because `Microsoft.Data.Sqlite` retries `SQLITE_BUSY` up to `CommandTimeout` and the single-statement seq insert may not visibly throw without the gate (§S1). **Sequence M1.2 and M1.3 together** and run this test after both.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~AppendJobEvent_ConcurrentAppends`
Expected: FAIL — compile error (`AppendJobEvent` undefined) before M1.3, then a genuine assertion failure (duplicate/gapped seqs) if the gate is absent under contention.

- [ ] **Step 3: Add the write gate (reentrancy-safe)**

```csharp
private readonly SemaphoreSlim _writeGate = new(1, 1);   // process-wide, NON-reentrant; HistoryLoader is single-host

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
```

**Gate only public entry points; nested calls MUST use ungated private cores or the non-reentrant semaphore self-deadlocks (§B2).** Concretely: `PruneAssetsNotIn` currently `await RemoveAsset(...)` per asset (`SqliteHistoryIndex.cs:364-372`) — extract `RemoveAssetCore(conn, exchange, dir, ct)` (no gate, takes the open connection), have public `RemoveAsset` do `using var _ = await _writeGate.LockAsync(ct); await using var conn = await Open(ct); await RemoveAssetCore(conn, …)`, and have `PruneAssetsNotIn` acquire the gate **once** then call `RemoveAssetCore` in a loop on its own connection. Wrap the body of every OTHER write method (`UpsertAsset`, `UpsertFeedStatus`, `ReplaceMonths`, `UpsertInstrumentMeta`, `SetDiscoveredFirstMonth`, `PruneFeedData`, `CreateJob`, `UpdateJob`, and the M1.3–M1.5 job methods) in `using var _ = await _writeGate.LockAsync(ct);` (from `AlgoTradeForge.Storage.Threading.SemaphoreSlimExtensions`) — audit each for nested `await this.<write>()` calls and route them to cores. Leave read methods ungated (WAL permits concurrent readers). Change `CreateJob`'s INSERT `'running'` → `'queued'`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~AppendJobEvent_ConcurrentAppends`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs
git commit -m "feat(index): process-wide write gate + busy_timeout; jobs born 'queued'"
```

### Task M1.3: `job_events` append/read + extended `IndexJobRow`

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs` (records + method sigs)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs` (add `JobsOptions` + `public JobsOptions Jobs { get; init; } = new();` — first consumed here via `MaxEventsPerJob`; see Shared Contracts for the class)
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.Jobs.cs` (partial — job/event methods move/land here)
- Modify (DI): the composition root that news `SqliteHistoryIndex` (grep `new SqliteHistoryIndex(` / `AddSingleton<IHistoryIndex`) — pass `options.Value.Jobs.MaxEventsPerJob` to the new constructor param
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs`

**Interfaces:**
- Consumes: M1.1 schema, M1.2 write gate.
- Produces: `AppendJobEvent`, `GetJobEventsAfter`, `GetLastEventSeq`; `IndexJobRow` extended with `FeedKey/CancelRequested/TouchedJson/RequestJson`; `JobEventRow` (Shared Contracts); `JobsOptions` on `HistoryLoaderOptions`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task JobEvents_Append_ReturnsMonotonicSeq_AndReadsAfter()
{
    var jobId = await _index.CreateJob("aggregation", Ct);
    Assert.Equal(1, await _index.AppendJobEvent(jobId, "started", "{}", Ct));
    Assert.Equal(2, await _index.AppendJobEvent(jobId, "progress", """{"done":1}""", Ct));
    Assert.Equal(3, await _index.AppendJobEvent(jobId, "progress", """{"done":2}""", Ct));

    var after1 = await _index.GetJobEventsAfter(jobId, 1, Ct);
    Assert.Equal(new[] { 2, 3 }, after1.Select(e => e.Seq));
    Assert.Equal("progress", after1[0].Kind);
    Assert.Equal(3, await _index.GetLastEventSeq(jobId, Ct));
    Assert.Empty(await _index.GetJobEventsAfter(jobId, 3, Ct));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~JobEvents_Append`
Expected: FAIL (method not defined).

- [ ] **Step 3: Implement**

Extend `IndexJobRow` and add records per Shared Contracts. Update `ReadJobRow` + **all** job SELECTs (`GetJob`, `GetActiveJob`, `GetLastJob`, `ListJobs`) to project the four new columns (`feed_key, cancel_requested, touched_json, request_json`) — `ReadJobRow` reads them positionally. Add a constructor param `int maxEventsPerJob = 500` to `SqliteHistoryIndex` (wired from `JobsOptions.MaxEventsPerJob` in DI). In the new partial file:

```csharp
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
```

`GetJobEventsAfter` → `SELECT seq, kind, payload_json, created_at FROM job_events WHERE job_id=$id AND seq>$after ORDER BY seq`. `GetLastEventSeq` → `SELECT COALESCE(MAX(seq),0) FROM job_events WHERE job_id=$id`. (Trimming never removes an event below a live SSE reader's `lastSentSeq` mid-stream in practice — the cap is far above any reader's in-flight window; a resumed reader that lost a trimmed progress event still gets the terminal event, which is never trimmed.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~JobEvents_Append`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.Jobs.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs
git commit -m "feat(index): job_events append/read + extended IndexJobRow"
```

### Task M1.4: Atomic feed-gate claim

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs`, `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.Jobs.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs`

**Interfaces:**
- Produces: `TryAcquireFeedGate(kind, feedKey, progressJson, requestJson, ct) → FeedGateOutcome`.

- [ ] **Step 1: Write the failing test** — N concurrent claims of one feed_key → exactly one Acquired.

```csharp
[Fact]
public async Task TryAcquireFeedGate_ConcurrentSameFeed_ExactlyOneAcquires()
{
    const string fk = "binance|BTCUSDT_perp|candles|1m";
    var outcomes = await Task.WhenAll(Enumerable.Range(0, 20)
        .Select(_ => _index.TryAcquireFeedGate("load", fk, "{}", "{}", Ct)));

    Assert.Single(outcomes, o => o is FeedGateOutcome.Acquired);
    Assert.Equal(19, outcomes.Count(o => o is FeedGateOutcome.Busy));
    var owner = outcomes.OfType<FeedGateOutcome.Acquired>().Single().JobId;
    Assert.All(outcomes.OfType<FeedGateOutcome.Busy>(), b => Assert.Equal(owner, b.ExistingJobId));

    // A different feed_key is not blocked.
    Assert.IsType<FeedGateOutcome.Acquired>(
        await _index.TryAcquireFeedGate("load", "binance|ETHUSDT|candles|1m", "{}", "{}", Ct));

    // Terminal state releases the gate — a new claim on fk now succeeds.
    await _index.UpdateJob(owner, "complete", ct: Ct);
    Assert.IsType<FeedGateOutcome.Acquired>(await _index.TryAcquireFeedGate("load", fk, "{}", "{}", Ct));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~TryAcquireFeedGate_Concurrent`
Expected: FAIL (method not defined).

- [ ] **Step 3: Implement — atomic INSERT-or-report-owner under the write gate**

```csharp
public async Task<FeedGateOutcome> TryAcquireFeedGate(string kind, string feedKey, string progressJson, string requestJson, CancellationToken ct = default)
{
    using var _ = await _writeGate.LockAsync(ct);
    await using var conn = await Open(ct);
    var id = Guid.NewGuid().ToString("N");
    var now = DateTime.UtcNow.ToString("O");
    await using var insert = conn.CreateCommand();
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
    var rows = await insert.ExecuteNonQueryAsync(ct);   // ux_jobs_active_feedkey is the backstop if the guard races
    if (rows == 1) return new FeedGateOutcome.Acquired(id);

    await using var owner = conn.CreateCommand();
    owner.CommandText = "SELECT id FROM index_jobs WHERE feed_key=$fk AND state IN ('queued','running') LIMIT 1";
    owner.Parameters.AddWithValue("$fk", feedKey);
    var existing = (string?)await owner.ExecuteScalarAsync(ct);
    return new FeedGateOutcome.Busy(existing ?? "unknown");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~TryAcquireFeedGate_Concurrent`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.Jobs.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs
git commit -m "feat(index): atomic feed-gate claim backed by UNIQUE partial index"
```

### Task M1.5: Cancel / touched / list / retention-delete

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs`, `src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.Jobs.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs`

**Interfaces:**
- Produces: `RequestCancel`, `SetTouched`, `ListInterruptedJobs`, `ListJobs`, `DeleteTerminalJobsBefore`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task Cancel_Touched_List_Retention_RoundTrip()
{
    var g = await _index.TryAcquireFeedGate("load", "binance|BTCUSDT|candles|1m", "{}", "{}", Ct);
    var id = Assert.IsType<FeedGateOutcome.Acquired>(g).JobId;

    await _index.SetTouched(id, "binance|BTCUSDT|candles|1m", "2024-03", Ct);
    await _index.RequestCancel(id, Ct);
    var row = await _index.GetJob(id, Ct);
    Assert.True(row!.CancelRequested);
    Assert.Contains("2024-03", row.TouchedJson);

    await _index.UpdateJob(id, "running", ct: Ct);
    Assert.Single(await _index.ListJobs("load", "running", Ct));

    // Mark interrupted → appears in ListInterruptedJobs with touched.
    await _index.UpdateJob(id, "interrupted", ct: Ct);
    var interrupted = await _index.ListInterruptedJobs(Ct);
    Assert.Equal(id, interrupted.Single().Id);
    Assert.Contains("2024-03", interrupted.Single().TouchedJson);

    // Retention: a terminal job with an old updated_at is deleted with its events.
    await _index.AppendJobEvent(id, "progress", "{}", Ct);
    await _index.UpdateJob(id, "complete", ct: Ct);
    var deleted = await _index.DeleteTerminalJobsBefore(DateTimeOffset.UtcNow.AddMinutes(1), Ct);
    Assert.Equal(1, deleted);
    Assert.Null(await _index.GetJob(id, Ct));
    Assert.Empty(await _index.GetJobEventsAfter(id, 0, Ct));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~Cancel_Touched_List_Retention`
Expected: FAIL (methods not defined).

- [ ] **Step 3: Implement**

- `RequestCancel` → `UPDATE index_jobs SET cancel_requested=1, updated_at=$now WHERE id=$id`.
- `SetTouched` → `UPDATE index_jobs SET touched_json=$j, updated_at=$now WHERE id=$id` where `$j = [{"feedKey":..,"month":..}]` (single-element array; overwrites).
- `ListInterruptedJobs` → `SELECT id, kind, feed_key, touched_json FROM index_jobs WHERE state='interrupted'`.
- `ListJobs(kind, state)` → `SELECT ... FROM index_jobs WHERE ($k IS NULL OR kind=$k) AND ($s IS NULL OR state=$s) ORDER BY created_at DESC` → full `IndexJobRow`s.
- `DeleteJob(jobId)` → in a transaction: `DELETE FROM job_events WHERE job_id=$id` then `DELETE FROM index_jobs WHERE id=$id`. Used by the M3.3 QueueFull rollback so a job the client saw as 503 leaves no phantom row (§S3).
- `DeleteTerminalJobsBefore(cutoff)` → in a transaction: `DELETE FROM job_events WHERE job_id IN (SELECT id FROM index_jobs WHERE state IN ('complete','error','cancelled') AND updated_at < $cutoff)` then the matching `DELETE FROM index_jobs ...`; return the second delete's row count. **Bind the cutoff as `cutoffUtc.UtcDateTime.ToString("O")`** so it string-compares correctly against stored `DateTime.UtcNow.ToString("O")` (`…Z`) values — a `DateTimeOffset.ToString("O")` would emit `…+00:00` and mis-order (NICE).

Add a matching `DeleteJob` test assertion (append to the round-trip test): `await _index.DeleteJob(otherId, Ct); Assert.Null(await _index.GetJob(otherId, Ct));`. All writes under `_writeGate` except `ListInterruptedJobs`/`ListJobs` (reads, ungated).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~Cancel_Touched_List_Retention`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Index/IHistoryIndex.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/Index/SqliteHistoryIndex.Jobs.cs tests/AlgoTradeForge.HistoryLoader.Tests/Index/SqliteHistoryIndexTests.cs
git commit -m "feat(index): cancel/touched/list/retention-delete job store operations"
```

---

## Milestone M2 — Doorbell + unified read/SSE endpoints

Builds the content-free liveness doorbell, the `IJobProgressSink`, and the kind-neutral `GET /jobs`, `GET /jobs/{id}`, `GET /jobs/{id}/progress` (SSE), `DELETE /jobs/{id}` endpoints. Depends on M1. Producers here have no live writers yet (M3 wires the workers) — tested with a fake writer.

### Task M2.1: `IJobEventSignal` doorbell

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobEventSignal.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/JobEventSignal.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/JobEventSignalTests.cs`

**Interfaces:**
- Produces: `IJobEventSignal` (Shared Contracts). Registered as a singleton.

- [ ] **Step 1: Write the failing test** — a reader that captures `Next` before any `Signal` still wakes; late reader of an evicted job does not hang.

```csharp
[Fact]
public async Task Signal_WakesReaderCapturedBeforeSignal()
{
    var sig = new JobEventSignal();
    var next = sig.Next("j1");                 // capture BEFORE signal
    Assert.False(next.IsCompleted);
    sig.Signal("j1");
    await next.WaitAsync(TimeSpan.FromSeconds(1));   // completes
    Assert.True(next.IsCompleted);
}

[Fact]
public async Task ManyReaders_AllWakeOnOneSignal()
{
    var sig = new JobEventSignal();
    var readers = Enumerable.Range(0, 8).Select(_ => sig.Next("j2")).ToArray();
    sig.Signal("j2");
    await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(1));   // all complete
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FullyQualifiedName~JobEventSignal`
Expected: FAIL (type not defined).

- [ ] **Step 3: Implement** — per-job swapped TCS, mirroring `AggregationJobRecord.AppendEvent`'s `Interlocked.Exchange`.

```csharp
public sealed class JobEventSignal : IJobEventSignal
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _cells = new();

    // A reader creates the cell so a Signal that arrives before it still completes THIS task.
    public Task Next(string jobId) =>
        _cells.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    // §S2: no-op when no reader has registered — do NOT GetOrAdd here, or a completed TCS is parked
    // and the next Next() returns an already-complete task → the SSE tail loop busy-spins between
    // events. Only swap+complete an EXISTING cell; the reader's own drain picks up the durable tail.
    public void Signal(string jobId)
    {
        if (!_cells.TryGetValue(jobId, out var prev)) return;
        var fresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cells.TryUpdate(jobId, fresh, prev);
        prev.TrySetResult();
    }

    public void Evict(string jobId) => _cells.TryRemove(jobId, out _);
}
```

Add a test asserting **no busy-spin when the first event precedes the first reader**: `sig.Signal("j3")` (no cell) is a no-op; a subsequent `sig.Next("j3")` returns an **incomplete** task (not an already-completed one).

- [ ] **Step 4: Run test to verify it passes** — Run the M2.1 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobEventSignal.cs src/AlgoTradeForge.HistoryLoader.Application/Jobs/JobEventSignal.cs tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/JobEventSignalTests.cs
git commit -m "feat(jobs): content-free per-job doorbell (IJobEventSignal)"
```

### Task M2.2: `IJobProgressSink` — the write seam

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobProgressSink.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/JobProgressSink.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobProgressSinkFactory.cs` + `JobProgressSinkFactory.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/JobProgressSinkTests.cs`

**Interfaces:**
- Consumes: `IHistoryIndex` (M1), `IJobEventSignal` (M2.1).
- Produces: `IJobProgressSink` (Shared Contracts); `IJobProgressSinkFactory.For(jobId) → IJobProgressSink`.

- [ ] **Step 1: Write the failing test** — `Report` writes a `progress` event + updates the row + signals; `Complete` evicts.

```csharp
[Fact]
public async Task Sink_Report_AppendsEventUpdatesRowSignals()
{
    var jobId = await _index.CreateJob("load", Ct);
    var signal = new JobEventSignal();
    var sink = new JobProgressSink(jobId, _index, signal);

    var woke = signal.Next(jobId);
    await sink.Report("""{"phase":"2024-03","done":3,"total":12}""", Ct);
    Assert.True(woke.IsCompleted);
    Assert.Equal(1, await _index.GetLastEventSeq(jobId, Ct));
    Assert.Contains("2024-03", (await _index.GetJob(jobId, Ct))!.ProgressJson);

    await sink.Complete("""{"ok":true}""", Ct);
    Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M2.2 filter; Expected: FAIL (type not defined).

- [ ] **Step 3: Implement** — each method: `UpdateJob(...)` then `AppendJobEvent(kind, payload)` then `signal.Signal(jobId)`; terminal methods also `signal.Evict(jobId)`. **Order matters: append (durable) before signal (liveness), signal before evict.** `JobProgressSinkFactory` news a `JobProgressSink` bound to the jobId.

- [ ] **Step 4: Run test to verify it passes** — Run M2.2 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobProgressSink.cs src/AlgoTradeForge.HistoryLoader.Application/Jobs/JobProgressSink.cs src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobProgressSinkFactory.cs src/AlgoTradeForge.HistoryLoader.Application/Jobs/JobProgressSinkFactory.cs tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/JobProgressSinkTests.cs
git commit -m "feat(jobs): IJobProgressSink write seam (row + event + doorbell)"
```

### Task M2.3: Unified envelope serializer + `GET /jobs`, `GET /jobs/{id}`

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobEndpoints.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobEnvelope.cs` (`IndexJobRow` → wire DTO)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (or wherever `MapLoadEndpoints` is called) to `MapJobEndpoints`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Endpoints/JobEndpointsTests.cs`

**Interfaces:**
- Consumes: `IHistoryIndex.ListJobs/GetJob`.
- Produces: `GET /api/v1/jobs?kind=&state=`, `GET /api/v1/jobs/{jobId}` returning the unified envelope (Shared Contracts). `JobEnvelope.From(IndexJobRow)` — reused by SSE (M2.4) and the alias endpoints (M5).

- [ ] **Step 1: Write the failing test** (endpoint-level, `InternalsVisibleTo` pattern like `LoadEndpoints`):

```csharp
[Fact]
public async Task GetJob_ReturnsUnifiedEnvelope()
{
    var jobId = (await _index.TryAcquireFeedGate("load", "binance|BTCUSDT|candles|1m",
        """{"phase":"2024-03","done":3,"total":12,"detail":{"current_month":"2024-03"}}""", "{}", Ct)
        as FeedGateOutcome.Acquired)!.JobId;

    var result = await JobEndpoints.GetJob(jobId, _index, Ct);
    var env = Assert.IsType<Ok<JobEnvelope>>(result).Value!;
    Assert.Equal("load", env.Kind);
    Assert.Equal("queued", env.State);
    Assert.Equal("binance|BTCUSDT|candles|1m", env.FeedKey);
    Assert.Equal(3, env.Progress!.Done);
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M2.3 filter; Expected: FAIL.

- [ ] **Step 3: Implement** `JobEnvelope` (record with `job_id, kind, state, feed_key, created_at, updated_at, error, progress`; `progress` parsed from `ProgressJson`), `JobEndpoints.MapJobEndpoints` (`GET /api/v1/jobs`, `GET /api/v1/jobs/{jobId}` → 404 `{code:"job_not_found",message}` when null), and register in Program. Wire `GetJob`/`GetJobs` as `internal static` for direct test.

- [ ] **Step 4: Run test to verify it passes** — Run M2.3 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobEndpoints.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobEnvelope.cs src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs tests/AlgoTradeForge.HistoryLoader.Tests/Endpoints/JobEndpointsTests.cs
git commit -m "feat(jobs): GET /jobs + GET /jobs/{id} unified envelope"
```

### Task M2.4: Unified SSE `GET /jobs/{id}/progress` over the durable tail

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobEndpoints.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobSseWriter.cs` (frame writer, adapted from `AggregationEndpoints.WriteSseFrameAsync`)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Endpoints/JobSseTests.cs`

**Interfaces:**
- Consumes: `IHistoryIndex.GetJobEventsAfter/GetLastEventSeq`, `IJobEventSignal.Next`.
- Produces: `GET /api/v1/jobs/{jobId}/progress` (SSE) — capture-before-drain, Last-Event-ID resume, terminal-close, durable-tail-after-restart.

- [ ] **Step 1: Write the failing test** — drive the tail loop against the store directly (no HTTP), asserting the capture-before-drain contract: an event appended between `Next` capture and the drain is still delivered.

```csharp
[Fact]
public async Task JobSse_TailLoop_DeliversEventAppendedBetweenCaptureAndDrain()
{
    var jobId = await _index.CreateJob("load", Ct);
    var signal = new JobEventSignal();
    var sink = new JobProgressSink(jobId, _index, signal);
    var frames = new List<(int Seq, string Kind)>();

    var loop = JobSseWriter.TailForTest(jobId, lastEventId: 0, _index, signal,
        emit: (seq, kind, _) => frames.Add((seq, kind)), ct: Ct);

    await sink.Report("""{"done":1}""", Ct);
    await sink.Complete("{}", Ct);         // terminal — loop must return
    await loop.WaitAsync(TimeSpan.FromSeconds(2));

    Assert.Equal(new[] { "progress", "complete" }, frames.Select(f => f.Kind));
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M2.4 filter; Expected: FAIL.

- [ ] **Step 3: Implement** the tail loop (extract the drain logic into `JobSseWriter.TailForTest` so it is unit-testable without HTTP; the endpoint wraps it with response headers + `context.RequestAborted`). Loop, mirroring `GetProgressSse`:

```
lastSent = lastEventId
while not aborted:
    next = signal.Next(jobId)                       // capture BEFORE drain
    fresh = await index.GetJobEventsAfter(jobId, lastSent)
    if lastSent == lastEventId && lastEventId > 0 && fresh.Count == 0 && await index.GetLastEventSeq(jobId) > 0:
        fresh = await index.GetJobEventsAfter(jobId, 0)     // replay past last-known
    foreach ev in fresh:
        emit(ev.Seq, ev.Kind, ev.PayloadJson); lastSent = ev.Seq
        if ev.Kind in {complete,error,cancelled}: return
    await next
```

Endpoint: 410 Gone when `GetJob` is null AND `GetLastEventSeq==0`. Headers/`Last-Event-ID` parse copied from `AggregationEndpoints`.

- [ ] **Step 4: Run test to verify it passes** — Run M2.4 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobEndpoints.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobSseWriter.cs tests/AlgoTradeForge.HistoryLoader.Tests/Endpoints/JobSseTests.cs
git commit -m "feat(jobs): unified SSE /jobs/{id}/progress over durable job_events tail"
```

### Task M2.5: `DELETE /jobs/{id}` — durable flag + live CTS trip

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobCancellationMap.cs` + `JobCancellationMap.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobEndpoints.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/JobCancellationMapTests.cs`

**Interfaces:**
- Produces: `IJobCancellationMap` (`CancellationToken Register(jobId, linkedTo)`, `void Trip(jobId)`, `void Remove(jobId)`) — singleton; `DELETE /api/v1/jobs/{jobId}` → `RequestCancel` + `Trip`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Trip_CancelsRegisteredToken()
{
    var map = new JobCancellationMap();
    var token = map.Register("j1", CancellationToken.None);
    Assert.False(token.IsCancellationRequested);
    map.Trip("j1");
    Assert.True(token.IsCancellationRequested);
    map.Remove("j1");   // idempotent, disposes CTS
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M2.5 filter; Expected: FAIL.

- [ ] **Step 3: Implement** `JobCancellationMap` (`ConcurrentDictionary<string, CancellationTokenSource>`; `Register` links to the host token via `CreateLinkedTokenSource`; `Trip` cancels; `Remove` disposes). `DELETE` handler: `await index.RequestCancel(id); map.Trip(id);` → 202/204. Register the map as a singleton.

- [ ] **Step 4: Run test to verify it passes** — Run M2.5 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobCancellationMap.cs src/AlgoTradeForge.HistoryLoader.Application/Jobs/JobCancellationMap.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/JobEndpoints.cs tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/JobCancellationMapTests.cs
git commit -m "feat(jobs): DELETE /jobs/{id} durable-flag + live-CTS cancellation"
```

---

## Milestone M3 — Migrate load + aggregation onto the store; delete registries; sweepers; storage fix

Extracts the two workers' bodies into `IArchiveLoadService`/`IAggregationService`, re-homes dispatch (wakeup channel), cancellation (CTS map), and retention (sweeper), deletes both registries, adds the interrupted-sweep, and makes `AtomicReplace` truly atomic. Largest milestone; the concurrency core lands here.

### Task M3.1: `IArchiveLoadService` (extract `LoadJobWorker.RunJob`, fold in F1 guard)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/IArchiveLoadService.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/ArchiveLoadService.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Collection/ArchiveLoadServiceTests.cs`

**Interfaces:**
- Consumes: `BackfillOrchestrator.TryRunSingle`, `IJobProgressSink`.
- Produces: `IArchiveLoadService.Run(ArchiveLoadRequest, IJobProgressSink, ct) → bool` (Shared Contracts). Reports progress via the sink; returns false on `symbol_busy`.

- [ ] **Step 1: Write the failing test** — F1: an interval-based transient feed with `Interval:""` must NOT throw (the phase-3a-class bug); it is guarded before `TryRunSingle`.

```csharp
[Fact]
public async Task Run_IntervalBasedFeed_WithEmptyInterval_DoesNotThrow_ReportsInvalidInterval()
{
    var svc = new ArchiveLoadService(_orchestrator, _options);
    var sink = new RecordingSink();
    var req = new ArchiveLoadRequest(_asset, FeedName: "candles", Interval: "", From: new(2024,1,1), To: new(2024,1,1));
    var ok = await svc.Run(req, sink, Ct);   // must not throw ArgumentException from IntervalParser
    Assert.False(ok);
    Assert.Equal("invalid_interval", sink.FailCode);
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M3.1 filter; Expected: FAIL.

- [ ] **Step 3: Implement** — move `LoadJobWorker.RunJob`'s body (transient-feed append + `TryRunSingle`) into `ArchiveLoadService.Run`, replacing `registry.OnStarted/OnCompleted/OnErrored` with `sink.Started/Complete/Fail`. **F1 guard:** before appending the transient feed, if the feed is interval-based (`!FeedNames.UsesMonthlyCompleteness(req.FeedName)`) and `req.Interval` is `""`/unparseable via `IntervalParser`, `await sink.Fail("invalid_interval", ...)` and return false. The `LoadJobProgress` progress reporter maps months-done/total into `sink.Report` with the load `detail` shape (Shared Contracts envelope).

- [ ] **Step 4: Run test to verify it passes** — Run M3.1 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Jobs/IArchiveLoadService.cs src/AlgoTradeForge.HistoryLoader.WebApi/Collection/ArchiveLoadService.cs tests/AlgoTradeForge.HistoryLoader.Tests/Collection/ArchiveLoadServiceTests.cs
git commit -m "feat(jobs): IArchiveLoadService extracted from LoadJobWorker + F1 interval guard"
```

### Task M3.2: `IAggregationService` (wrap `AggregationPipeline.Run`)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/IAggregationService.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Aggregation/AggregationService.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/AggregationServiceTests.cs`

**Interfaces:**
- Consumes: `AggregationPipeline.Run(job, onProgress, ct)`, `IJobProgressSink`.
- Produces: `IAggregationService.Run(AggregationRunRequest, IJobProgressSink, ct)`.

- [ ] **Step 1: Write the failing test** — `Run` routes pipeline progress into `sink.Report` and calls `sink.Complete` with the result.

```csharp
[Fact]
public async Task Run_RoutesPipelineProgress_ToSink_AndCompletes()
{
    var svc = new AggregationService(_scopeFactory);   // resolves AggregationPipeline per-scope
    var sink = new RecordingSink();
    await svc.Run(new AggregationRunRequest(_smallJob), sink, Ct);
    Assert.True(sink.Completed);
    Assert.NotEmpty(sink.Reports);   // at least one progress event routed
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M3.2 filter; Expected: FAIL.

- [ ] **Step 3: Implement** — `AggregationService.Run` creates a DI scope, resolves `AggregationPipeline`, calls `pipeline.Run(req.Job, onProgress: ev => sink.Report(<agg detail json>), ct)`, then `sink.Complete(<result json>)`. Cancellation and error mapping mirror `AggregationWorkerHost.RunWorkerAsync`'s catch arms but route to `sink.Cancel/Fail` instead of `registry.On*`.

- [ ] **Step 4: Run test to verify it passes** — Run M3.2 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Jobs/IAggregationService.cs src/AlgoTradeForge.HistoryLoader.WebApi/Aggregation/AggregationService.cs tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/AggregationServiceTests.cs
git commit -m "feat(jobs): IAggregationService wrapping AggregationPipeline"
```

### Task M3.3: Wakeup channel + load path on the store; delete `LoadJobRegistry`

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobWakeupQueue.cs` + `JobWakeupQueue.cs` (per-kind bounded `Channel<string>`)
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LoadRequestRehydrator.cs` (`IndexJobRow.RequestJson` → `ArchiveLoadRequest`)
- Rewrite: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LoadJobWorker.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/LoadEndpoints.cs`
- Modify (DI): `src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs:198` (drop `AddSingleton<ILoadJobRegistry, LoadJobRegistry>`; add keyed `IJobWakeupQueue "load"`), `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (register `LoadRequestRehydrator`; `LoadJobWorker` already hosted)
- Delete (verify each with grep first — the registry folder holds exactly these): `Application/Archive/Jobs/LoadJobRegistry.cs`, `ILoadJobRegistry.cs`, `LoadJobRecord.cs`, `LoadJobSnapshot.cs`, `LoadEnqueueOutcome.cs`, `LoadJob.cs`, `LoadJobState.cs`; and `WebApi/Collection/LoadJobProgress.cs` (**NOT** in Application/Archive/Jobs — it lives in WebApi/Collection). Keep nothing from the registry.
- Rewrite tests: delete `tests/.../Archive/LoadJobRegistryTests.cs`; **rewrite** `tests/.../Archive/LoadEndpointValidationTests.cs` (references `ILoadJobRegistry`/`LoadEnqueueOutcome`) to drive the new `TryAcquireFeedGate` path.
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Collection/LoadJobWorkerTests.cs`

**Interfaces:**
- Consumes: `TryAcquireFeedGate`, `IArchiveLoadService`, `IJobProgressSinkFactory`, `IJobCancellationMap`, `ICollectionPlanSource`, `JobsOptions.WakeupChannelDepth`.
- Produces: `IJobWakeupQueue` (`bool TryEnqueue(string jobId)`, `IAsyncEnumerable<string> Reader(ct)`, `int SeedFromQueued(IEnumerable<string> jobIds)`), one keyed instance per gated kind (`"load"`, `"aggregation"`, `"materialize"`); `LoadRequestRehydrator.Rehydrate(IndexJobRow) → ArchiveLoadRequest`.

- [ ] **Step 1: Write the failing test** — the worker rehydrates a queued row (with persisted `request_json`) into an `ArchiveLoadRequest`, runs the service, and the row reaches `complete`.

```csharp
[Fact]
public async Task Worker_RehydratesQueuedRow_RunsService_ReachesComplete()
{
    var reqJson = LoadRequestRehydrator.Serialize("binance", "BTCUSDT", "CryptoPerpetual", "candles", "1m", new(2024,1,1), new(2024,1,1));
    var jobId = (await _index.TryAcquireFeedGate("load", "binance|BTCUSDT_perp|candles|1m", "{}", reqJson, Ct) as FeedGateOutcome.Acquired)!.JobId;
    _wakeup.TryEnqueue(jobId);
    await _worker.DrainOnceForTest(Ct);        // rehydrate + run (fake IArchiveLoadService returns true)
    Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
    Assert.Equal("1m", _fakeArchiveLoad.LastRequest!.Interval);   // rehydration carried the interval + dates
}

[Fact]
public void SeedFromQueued_EnqueuesAllQueuedRows() => Assert.Equal(2, _wakeup.SeedFromQueued(new[] { "a", "b" }));
```

- [ ] **Step 2: Run test to verify it fails** — Run M3.3 filter; Expected: FAIL.

- [ ] **Step 3: Implement**
  - `JobWakeupQueue`: bounded `Channel<string>(WakeupChannelDepth)`; `TryEnqueue` → `Writer.TryWrite` (false = full → caller returns `QueueFull`); `Reader` → `ReadAllAsync`; `SeedFromQueued` writes each id, returns count.
  - `LoadRequestRehydrator`: `Serialize(exchange, symbol, assetType, feed, interval, from, to)` → JSON; `Rehydrate(row)` deserializes and **resolves the `CollectionAsset` from `ICollectionPlanSource.Current`** by `(exchange, apiSymbol, assetType)` — the same lookup `PostLoad` already does (§B3: the row's `feed_key` alone cannot rebuild the asset/dates).
  - `LoadJobWorker`: on start, `SeedFromQueued(index.ListJobs("load","queued").Select(j => j.Id))`; loop `await foreach (jobId in wakeup.Reader(ct))`: read the row, `UpdateJob(jobId,"running")`, `map.Register(jobId, stoppingToken)`, build sink via factory, `req = rehydrator.Rehydrate(row)`, `await archiveLoad.Run(req, sink, linkedCt)`, `map.Remove(jobId)`. Keep the `IsTrueShutdown` filter (unchanged, correct).
  - `LoadEndpoints.PostLoad` (now `async Task<IResult>`): after the existing symbol-declared/validation checks, build the 4-part `feedKey = $"{body.Exchange}|{assetDir}|{body.FeedName}|{body.Interval}"` and `reqJson = LoadRequestRehydrator.Serialize(...)`; `await index.TryAcquireFeedGate("load", feedKey, initialProgressJson, reqJson, ct)` → `Acquired(id)` ⇒ `wakeup.TryEnqueue(id)` ? `202 {job_id}` : (`await index.DeleteJob(id, ct)`; `503 {code:"queue_full"}`) — **use `DeleteJob` (§S3), not an `error` row, so a client that saw 503 leaves no phantom job**; `Busy(existing)` ⇒ `409 {code:"feed_busy", active_job_id:existing}`. **§S5 behavior change (intentional, document in the commit body):** the old synchronous `ActiveJobForSymbol` → `409 symbol_busy` (symbol-level) is GONE — gating is now feed-level, so a second *different* feed on a busy symbol is accepted and, if the underlying backfill lock is held, fails asynchronously via the service; only same-feed collisions return `409 feed_busy`. Keep `symbol_not_declared` (422) — F2 `symbol_blocked` upgrade is M5.
  - Delete the registry files + rewrite the two affected tests.

- [ ] **Step 4: Run test to verify it passes** — Run M3.3 filter + full suite `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`; Expected: PASS (every deleted-type reference removed — grep `ILoadJobRegistry`/`LoadEnqueueOutcome` returns nothing).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Jobs/IJobWakeupQueue.cs src/AlgoTradeForge.HistoryLoader.Application/Jobs/JobWakeupQueue.cs src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LoadRequestRehydrator.cs src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LoadJobWorker.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/LoadEndpoints.cs src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs tests/AlgoTradeForge.HistoryLoader.Tests/Collection/LoadJobWorkerTests.cs tests/AlgoTradeForge.HistoryLoader.Tests/Archive/LoadEndpointValidationTests.cs
git rm src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobRegistry.cs src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/ILoadJobRegistry.cs src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobRecord.cs src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobSnapshot.cs src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadEnqueueOutcome.cs src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJob.cs src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobState.cs src/AlgoTradeForge.HistoryLoader.WebApi/Collection/LoadJobProgress.cs tests/AlgoTradeForge.HistoryLoader.Tests/Archive/LoadJobRegistryTests.cs
git commit -m "feat(jobs): load path on durable store + wakeup channel + rehydrator; delete LoadJobRegistry"
```

### Task M3.4: Aggregation path on the store; delete `AggregationJobRegistry`

**Files:**
- Rewrite: `src/AlgoTradeForge.HistoryLoader.WebApi/Aggregation/AggregationWorkerHost.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Aggregation/AggregationRequestRehydrator.cs` (`request_json` → `AggregationJob`)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/AggregationEndpoints.cs` (creation → store; SSE/snapshot become aliases in M5)
- Modify (DI): `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs:97` (drop `AddSingleton<IAggregationJobRegistry, AggregationJobRegistry>`; add keyed `IJobWakeupQueue "aggregation-timebar"` + `"aggregation-tick"`; register the rehydrator)
- Delete (grep to confirm the folder's 8 files; **KEEP the two queues + their interfaces** — `IAggregationJobQueue`/`AggregationJobQueue`, `IAggregationTickJobQueue`/`AggregationTickJobQueue` survive as the pool-split payload channels): `Aggregation/Jobs/AggregationJobRegistry.cs`, `IAggregationJobRegistry.cs`, `AggregationJobRecord.cs`, and the snapshot/outcome types the registry owns (`AggregationJobSnapshot`, `EnqueueOutcome` — grep each).
- Rewrite tests: delete `AggregationJobRegistryTests.cs`, `AggregationJobRecordTests.cs`; **rewrite** `tests/.../Aggregation/AggregationEndpointAssetResolutionTests.cs` (references the registry) onto the `TryAcquireFeedGate` path.
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/AggregationWorkerHostTests.cs`

**Interfaces:**
- Consumes: `IAggregationService`, `IJobWakeupQueue` keyed `"aggregation-timebar"` + `"aggregation-tick"`, `IJobCancellationMap`, `IJobProgressSinkFactory`, the existing typed time-bar/tick payload channels.
- Produces: aggregation jobs fully on the durable store; `AggregationRequestRehydrator.Rehydrate(row) → AggregationJob`.

- [ ] **Step 1: Write the failing test** — a queued aggregation job rehydrates, drains, runs the service, reaches `complete`; a cancel via the map trips it to `cancelled`.

```csharp
[Fact]
public async Task AggHost_RehydratesQueuedJob_Completes_AndCancelTrips()
{
    var reqJson = AggregationRequestRehydrator.Serialize(_aggregateRequestContext);   // source/output/threshold/etc
    var jobId = (await _index.TryAcquireFeedGate("aggregation", "binance|BTCUSDT|EqV_1k|", "{}", reqJson, Ct) as FeedGateOutcome.Acquired)!.JobId;
    _timebarWakeup.TryEnqueue(jobId);
    await _host.DrainOnceForTest(Ct);
    Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M3.4 filter; Expected: FAIL.

- [ ] **Step 3: Implement** — **single durable dispatch, two pools.** The wakeup queues carry jobIds (durable row = truth); there is one per pool (`"aggregation-timebar"`, `"aggregation-tick"`) so the pool split survives. Flow: POST → `TryAcquireFeedGate("aggregation", outputFeedKey, progressJson, reqJson)` → `Acquired` ⇒ enqueue the jobId to the timebar-or-tick wakeup by source type (tick source → tick pool) ⇒ 202; on boot, `SeedFromQueued(ListJobs("aggregation","queued"))` routes each queued row to the right pool by its rehydrated source type. Each pool worker: `await foreach (jobId in wakeup.Reader)` → read row → `UpdateJob("running")` → `map.Register(jobId, stoppingToken)` → `job = rehydrator.Rehydrate(row)` → `aggregationService.Run(new(job), sink, linkedCt)`. The registry's typed `Channel<AggregationJob>` payload channels are **retired as the dispatch mechanism** — the wakeup jobId queue replaces them; a rehydrated `AggregationJob` is the payload. Preserve the user-cancel (`sink.Cancel("user_cancelled")`) vs host-shutdown (`sink.Fail("host_shutdown", retryable)`) distinction from the old catch arms. `AggregationEndpoints` POST builds `reqJson` via the rehydrator's `Serialize`; `423`→`409 feed_busy` normalization is M5. Delete registry/record files + their tests; rewrite the resolution test.

- [ ] **Step 4: Run test to verify it passes** — Run M3.4 filter + full suite; Expected: PASS (grep `IAggregationJobRegistry` returns nothing).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Aggregation/AggregationWorkerHost.cs src/AlgoTradeForge.HistoryLoader.WebApi/Aggregation/AggregationRequestRehydrator.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/AggregationEndpoints.cs src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/AggregationWorkerHostTests.cs tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/AggregationEndpointAssetResolutionTests.cs
git rm src/AlgoTradeForge.HistoryLoader.Application/Aggregation/Jobs/AggregationJobRegistry.cs src/AlgoTradeForge.HistoryLoader.Application/Aggregation/Jobs/IAggregationJobRegistry.cs src/AlgoTradeForge.HistoryLoader.Application/Aggregation/Jobs/AggregationJobRecord.cs tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/Jobs/AggregationJobRegistryTests.cs tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/Jobs/AggregationJobRecordTests.cs
git commit -m "feat(jobs): aggregation path on durable store + rehydrator; delete AggregationJobRegistry"
```

### Task M3.5: `JobRetentionSweeper`

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Jobs/JobRetentionSweeper.cs` (BackgroundService)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (register as a hosted service — §S8)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/JobRetentionSweeperTests.cs`

**Interfaces:**
- Consumes: `IHistoryIndex.DeleteTerminalJobsBefore`, `JobsOptions` (added in M1.3).
- Produces: periodic deletion of terminal jobs + events past `RetentionMinutes`. (The per-job `job_events` progress-cap is already enforced inline by `AppendJobEvent`, M1.3 §S6 — this sweeper only does whole-job terminal retention.)

- [ ] **Step 1: Write the failing test** — one sweep pass deletes a terminal job older than the window, keeps a fresh one.

```csharp
[Fact]
public async Task Sweep_DeletesExpiredTerminalJobs_KeepsFresh()
{
    var old = await _index.CreateJob("load", Ct); await _index.UpdateJob(old, "complete", ct: Ct);
    // (force old.updated_at back via a direct UPDATE in the test helper)
    await BackdateUpdatedAt(old, DateTimeOffset.UtcNow.AddHours(-2));
    var fresh = await _index.CreateJob("load", Ct); await _index.UpdateJob(fresh, "complete", ct: Ct);

    await JobRetentionSweeper.SweepOnceForTest(_index, TimeSpan.FromMinutes(30), Ct);
    Assert.Null(await _index.GetJob(old, Ct));
    Assert.NotNull(await _index.GetJob(fresh, Ct));
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M3.5 filter; Expected: FAIL.

- [ ] **Step 3: Implement** — `JobsOptions` on `HistoryLoaderOptions`; `SweepOnceForTest(index, window, ct)` = `index.DeleteTerminalJobsBefore(now - window, ct)`; `ExecuteAsync` loops every `RetentionSweepMinutes` calling it, with the `IsTrueShutdown`-style OCE filter.

- [ ] **Step 4: Run test to verify it passes** — Run M3.5 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Jobs/JobRetentionSweeper.cs src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/JobRetentionSweeperTests.cs
git commit -m "feat(jobs): JobRetentionSweeper prunes terminal jobs + events"
```

### Task M3.6: `InterruptedJobSweeper` + `SetTouched`-before-fetch wiring

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Jobs/InterruptedJobSweeper.cs` (runs on boot, before the reconciler's first convergence)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/ArchiveLoadService.cs` + the scheduled-collector month loop to call `SetTouched(jobId, feedKey, month)` **before** collecting each month
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/InterruptedJobSweeperTests.cs`

**Interfaces:**
- Consumes: `ListInterruptedJobs`, `month_partitions` rows, the drift/completeness helpers (`MonthCoverageMath`/`complete_months_json`), `IFileStorage`.
- Produces: interrupted in-flight months reconciled so boot convergence re-collects them.

- [ ] **Step 1: Write the failing test** — a `touched` month whose file is absent (mid-`AtomicReplace` crash) gets its stale `month_partitions` row deleted + orphan `.tmp` removed.

```csharp
[Fact]
public async Task Sweep_MissingFileForTouchedMonth_DeletesStaleRowAndOrphanTmp()
{
    var jobId = (await _index.TryAcquireFeedGate("load", "binance|BTCUSDT|candles|1m", "{}", "{}", Ct) as FeedGateOutcome.Acquired)!.JobId;
    await _index.SetTouched(jobId, "binance|BTCUSDT|candles|1m", "2024-03", Ct);
    await _index.UpdateJob(jobId, "interrupted", ct: Ct);
    // month_partitions has a stale row for 2024-03 but the CSV is absent; an orphan .tmp sits beside it.
    await SeedStaleMonthRow("binance", "BTCUSDT", "candles", "1m", "2024-03");
    CreateOrphanTmp("binance/BTCUSDT/candles/2024-03.csv.tmp-abc");

    await _sweeper.SweepOnceForTest(Ct);

    Assert.Empty(await _index.GetMonths("binance", "BTCUSDT", "candles", "1m", Ct));
    Assert.False(OrphanTmpExists());
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M3.6 filter; Expected: FAIL.

- [ ] **Step 3: Implement** — for each `ListInterruptedJobs` row, parse `touched_json`'s `(feedKey, month)`; **split the 4-part `feedKey` into `(exchange, dir, feedName, interval)`** (the exchange is segment 0 — §S9, so `month_partitions`/`GetMonths` lookups are exact and never cross venues); resolve the on-disk partition path; if absent → delete the `month_partitions` row + any sibling `.tmp-*`; else invalidate that month's completeness (clear it from `complete_months_json` / force a targeted re-scan) so convergence cannot read it as complete. Wire `SetTouched(jobId, feedKey, month)` **before** each month fetch in `ArchiveLoadService` and the scheduled collector's month loop (the signal for row-math-blind feeds). Do **not** re-enqueue here — the 3a kick owns that. **§S8 ordering:** register `InterruptedJobSweeper` in `Program.cs` as a hosted service that runs **before** `DesiredStateService`'s first convergence — either as an explicit boot step invoked by the startup pipeline, or via hosted-service registration order + an internal gate the reconciler awaits (state the chosen mechanism; the sweep MUST complete before the first kick or the incomplete month is read as complete).

- [ ] **Step 4: Run test to verify it passes** — Run M3.6 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Jobs/InterruptedJobSweeper.cs src/AlgoTradeForge.HistoryLoader.WebApi/Collection/ArchiveLoadService.cs src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/InterruptedJobSweeperTests.cs
git commit -m "feat(jobs): interrupted-sweep reconciles in-flight month (missing-file/completeness) + SetTouched-before-fetch"
```

### Task M3.7: Make `LocalFileStorage.AtomicReplace` truly atomic

**Files:**
- Modify: `src/AlgoTradeForge.Storage/LocalFileStorage.cs:235-239` (and the class-doc + inline comments at 12-16 / 230-234)
- Test: `tests/AlgoTradeForge.Infrastructure.Tests/IO/LocalFileStorageContractTests.cs` (the storage tests live HERE — there is no `AlgoTradeForge.Storage.Tests` project, §B8)

**Interfaces:**
- Produces: `AtomicReplace` = `File.Move(src, dst, overwrite:true)` with a one-shot IOException retry (mirrors `PartitionFileWriter.ReplacePartition`), closing the delete-then-move absent window for all callers.

- [ ] **Step 1: Write the failing test** — replacing an existing file while a reader holds it open (the way real readers open: `FileShare.ReadWrite | FileShare.Delete`) succeeds; the destination is never absent (no `File.Delete` window).

```csharp
[Fact]
public async Task WriteAllLines_ReplacingOpenFile_NeverLeavesDestinationAbsent()
{
    var storage = new LocalFileStorage(_opts);
    await storage.WriteAllLines("f.csv", new[] { "a" }, Ct);
    // Real readers (ReadWithEtag, PartitionFileWriter consumers) open with Delete-sharing;
    // FileShare.Read WITHOUT Delete would block MoveFileEx even after the fix (§B8) — that would be
    // a bad test, not a code bug. Open the reader exactly as production does.
    using var reader = new FileStream(Resolve("f.csv"), FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);
    await storage.WriteAllLines("f.csv", new[] { "b", "c" }, Ct);   // must not throw; dst always present
    Assert.Equal(new[] { "b", "c" }, await storage.ReadAllLines("f.csv", Ct));
}
```

- [ ] **Step 2: Run test to verify it fails** — Run `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/ --filter FullyQualifiedName~ReplacingOpenFile`; Expected: FAIL (the current delete-then-move exposes the absent window / can throw).

- [ ] **Step 3: Implement**

```csharp
private static void AtomicReplace(string src, string dst)
{
    try { File.Move(src, dst, overwrite: true); }
    catch (IOException)
    {
        // Windows: a concurrent reader can briefly fail the replace; one short retry (matches PartitionFileWriter).
        Thread.Sleep(500);
        File.Move(src, dst, overwrite: true);
    }
}
```

Update the class-doc comment (12-16) to "atomic via `.tmp` + `File.Move(overwrite:true)`". Replace the inline comment at 230-234 — its claim that Move-overwrite "denies access even with FileShare.Delete" is contradicted by `PartitionFileWriter.ReplacePartition`'s proven `overwrite:true`+retry; note instead that on post-1903 Windows `MoveFileEx(REPLACE_EXISTING)` follows POSIX rename semantics and the one-shot retry covers the rare open-handle race. Do not merely delete the comment — leave the corrected rationale so a future reader doesn't revert to delete-then-move.

- [ ] **Step 4: Run test to verify it passes** — Run the storage-tests filter; then the full `AlgoTradeForge.Infrastructure.Tests` + HistoryLoader suites (CAS/feed-schema writers also use `AtomicReplace`). Expected: PASS, no regression.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.Storage/LocalFileStorage.cs tests/AlgoTradeForge.Infrastructure.Tests/IO/LocalFileStorageContractTests.cs
git commit -m "fix(storage): AtomicReplace uses File.Move(overwrite) — closes delete-then-move absent window"
```

---

## Milestone M4 — Materialize composite

Single resumable `kind=materialize` row driving load→aggregate (derived) or load-only (on-demand collected), via the extracted services. Depends on M3.

### Task M4.1: Materialize resolution + `POST /api/v1/materialize`

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/MaterializePlan.cs` (resolves a target feed into 1–2 stages)
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/MaterializeEndpoints.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/MaterializePlanTests.cs`

**Interfaces:**
- Consumes: `ICollectionPlanSource` (derived vs on-demand from the group plan), `TryAcquireFeedGate`, `IJobWakeupQueue("materialize")`.
- Produces: `MaterializePlan.Resolve(plan, exchange, symbol, feed, range) → MaterializePlan` (first param is the `ICollectionPlanSource.Current` snapshot the test passes as `_plan`) with `IReadOnlyList<MaterializeStage>` (`Load(sourceFeedKey)` and optional `Aggregate(outputFeedKey)`); `POST /api/v1/materialize` → 202 `{ job_id, location: "/api/v1/jobs/{id}/progress" }`. The endpoint serializes the materialize request into `request_json` and passes it to `TryAcquireFeedGate("materialize", outputFeedKey, initialProgressJson{stagesTotal}, requestJson, ct)`.

- [ ] **Step 1: Write the failing test** — a derived feed resolves to two stages (load source → aggregate output); an on-demand collected feed resolves to one (load).

```csharp
[Fact]
public void Resolve_DerivedFeed_TwoStages_OnDemandFeed_OneStage()
{
    var derived = MaterializePlan.Resolve(_plan, "binance", "BTCUSDT", "EqV_1k", range: null);
    Assert.Equal(2, derived.Stages.Count);
    Assert.IsType<MaterializeStage.Load>(derived.Stages[0]);
    Assert.IsType<MaterializeStage.Aggregate>(derived.Stages[1]);

    var onDemand = MaterializePlan.Resolve(_plan, "binance", "BTCUSDT", "agg-trades", range: null);
    Assert.Single(onDemand.Stages);
    Assert.IsType<MaterializeStage.Load>(onDemand.Stages[0]);
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M4.1 filter; Expected: FAIL.

- [ ] **Step 3: Implement** — `MaterializePlan.Resolve` reads the group plan: if the feed is a `derived` entry, stages = `[Load(source feedKey), Aggregate(output feedKey)]`; if an `on-demand` collected feed, stages = `[Load(feedKey)]`; unknown → throw a resolution error the endpoint maps to 422 `{code:"feed_not_materializable"}`. Endpoint: resolve → `TryAcquireFeedGate("materialize", outputFeedKey, initialProgressJson{stagesTotal})` → `Acquired` ⇒ `wakeup.TryEnqueue` + 202; `Busy` ⇒ 409 `feed_busy`.

- [ ] **Step 4: Run test to verify it passes** — Run M4.1 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Jobs/MaterializePlan.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/MaterializeEndpoints.cs src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/MaterializePlanTests.cs
git commit -m "feat(materialize): POST /materialize + 1-or-2-stage plan resolution"
```

### Task M4.2: `MaterializeWorkerHost` — sequential stages, composite progress, resume-from-stage

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.WebApi/Jobs/MaterializeWorkerHost.cs`
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Jobs/MaterializeProgressSink.cs` (wraps a base sink; rewrites stage progress into the composite envelope)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/MaterializeWorkerHostTests.cs`

**Interfaces:**
- Consumes: `IJobWakeupQueue("materialize")`, `IArchiveLoadService`, `IAggregationService`, `IJobProgressSinkFactory`, `IJobCancellationMap`, `MaterializePlan`, `ICollectionPlanSource`.
- Produces: materialize jobs that run each stage in order, advancing `progress_json.stageIndex`; on boot the host seeds BOTH `state='queued'` AND `state='interrupted'` materialize rows (resetting interrupted → queued) so a crashed composite resumes at its persisted `stageIndex` (§S7).

- [ ] **Step 1: Write the failing test** — a two-stage job runs load then aggregate and completes; a job resumed at `stageIndex=1` skips the load stage; an `interrupted` materialize row is re-seeded on boot.

```csharp
[Fact]
public async Task Materialize_RunsBothStages_Completes()
{
    var jobId = await SeedMaterializeJob(stagesTotal: 2, stageIndex: 0, feedKey: "binance|BTCUSDT|EqV_1k|");
    _wakeup.TryEnqueue(jobId);
    await _host.DrainOnceForTest(Ct);
    Assert.Equal("complete", (await _index.GetJob(jobId, Ct))!.State);
    Assert.True(_fakeLoad.Ran && _fakeAgg.Ran);
}

[Fact]
public async Task Materialize_ResumedAtStage1_SkipsLoad()
{
    var jobId = await SeedMaterializeJob(stagesTotal: 2, stageIndex: 1, feedKey: "binance|BTCUSDT|EqV_1k|");
    _wakeup.TryEnqueue(jobId);
    await _host.DrainOnceForTest(Ct);
    Assert.False(_fakeLoad.Ran);
    Assert.True(_fakeAgg.Ran);
}

[Fact]
public async Task Boot_ReseedsInterruptedMaterialize()
{
    var jobId = await SeedMaterializeJob(stagesTotal: 2, stageIndex: 1, feedKey: "binance|BTCUSDT|EqV_1k|");
    await _index.UpdateJob(jobId, "interrupted", ct: Ct);
    var n = await _host.SeedOnBootForTest(Ct);   // resets interrupted→queued + enqueues
    Assert.Equal(1, n);
    Assert.Equal("queued", (await _index.GetJob(jobId, Ct))!.State);
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M4.2 filter; Expected: FAIL.

- [ ] **Step 3: Implement** — **boot seed:** `ListJobs("materialize","queued")` ∪ `ListJobs("materialize","interrupted")`; for interrupted rows `UpdateJob(id,"queued")` first, then enqueue all (§S7 — nothing else re-triggers a crashed composite; the 3a kick knows feeds, not materialize rows). **Drain:** for each job read `progress_json` (stageIndex/stagesTotal) + `request_json`, resolve `MaterializePlan`; register CTS; for `i in stageIndex..stagesTotal-1`: build `MaterializeProgressSink(baseSink, stageIndex:i, stagesTotal)` and run the stage's service; after each stage `UpdateJob(progress with stageIndex=i+1)`. Final stage success → `sink.Complete`; stage failure → `sink.Fail`; cancel → `sink.Cancel`. **§B5 — stages do NOT re-acquire feed-gates.** The materialize job already holds the OUTPUT feed-gate (claimed at M4.1); the aggregate stage's key EQUALS the job's own key, so a re-claim would return `Busy(ownJobId)` and self-fail. Source-load concurrency (a manual load of the same source racing the load stage) is already serialized by the existing 3a `SymbolCollector.CollectFeed` per-`(assetDir,feed,interval)` skip-gate (3a spec §3.4) + the atomic wholesale month write + watermark dedup — no stage-level job-gate is needed or correct.

- [ ] **Step 4: Run test to verify it passes** — Run M4.2 filter; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Jobs/MaterializeWorkerHost.cs src/AlgoTradeForge.HistoryLoader.Application/Jobs/MaterializeProgressSink.cs src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs tests/AlgoTradeForge.HistoryLoader.Tests/Jobs/MaterializeWorkerHostTests.cs
git commit -m "feat(materialize): worker host — sequential stages, composite progress, resume-from-stage"
```

---

## Milestone M5 — Endpoint normalization (F2/F3) + snapshot aliases

Small, mostly-cosmetic contract cleanup. Depends on M3.

### Task M5.1: `symbol_blocked` (F2) + `{code,message}` error normalization (F3) + snapshot aliases

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/LoadEndpoints.cs` (F2 + alias `GET /loads/{id}` → unified envelope), `BackfillEndpoints.cs`, `AggregationEndpoints.cs` (alias `GET /aggregations/{id}` + `/progress` → unified), `StatusEndpoints.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Endpoints/ErrorNormalizationTests.cs`

**Interfaces:**
- Consumes: `ICollectionPlanSource` (to tell blocked-but-declared from not-declared), `JobEnvelope.From` (M2.3).
- Produces: `POST /loads` returns `422 {code:"symbol_blocked"}` for a declared-but-blocked asset; all four endpoints emit `{code, message}`; the aggregation POST's `423`→`409 feed_busy` normalization (deferred from M3.4) lands here; `GET /loads/{id}` and `GET /aggregations/{id}` return the unified envelope.

- [ ] **Step 1: Write the failing test** — note `PostLoad` is `async Task<IResult>` after M3.3 and takes the wakeup queue (§S4); await it and pass the new dependencies.

```csharp
[Fact]
public async Task PostLoad_DeclaredButBlockedAsset_Returns_SymbolBlocked()
{
    // plan has the symbol as a `blocked` tuple (excluded from CollectionPlan, unknown precision)
    var result = await LoadEndpoints.PostLoad(_blockedBody, _options, _matRegistry, _index, _wakeup, _blockedPlanSource, Ct);
    var problem = Assert.IsType<JsonHttpResult<ErrorBody>>(result);
    Assert.Equal(StatusCodes.Status422UnprocessableEntity, problem.StatusCode);
    Assert.Equal("symbol_blocked", problem.Value!.Code);
}
```

- [ ] **Step 2: Run test to verify it fails** — Run M5.1 filter; Expected: FAIL.

- [ ] **Step 3: Implement** — introduce a shared `ErrorBody(string Code, string Message)` record + `Results.Json(new ErrorBody(...), statusCode)` helper; replace each endpoint's ad-hoc `{error=...}`/prose bodies with it (backfill's 400 prose gets a `symbol_not_configured` code; aggregation's `asset_not_configured` and status's `"Symbol not found"` get codes; the aggregation POST feed-busy becomes `409 {code:"feed_busy"}` — the M3.4-deferred normalization). In `PostLoad`, distinguish: symbol present in the plan's *blocked* set → `422 symbol_blocked`; absent entirely → `422 symbol_not_declared`. `GET /loads/{id}` and `GET /aggregations/{id}` delegate to `JobEnvelope.From(await index.GetJob(id))` (404 `{code:"job_not_found"}` when null); `GET /aggregations/{id}/progress` 308-redirects (or thin-aliases) to `/jobs/{id}/progress`.

- [ ] **Step 4: Run test to verify it passes** — Run M5.1 filter + full suite; Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/LoadEndpoints.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/BackfillEndpoints.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/AggregationEndpoints.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/StatusEndpoints.cs tests/AlgoTradeForge.HistoryLoader.Tests/Endpoints/ErrorNormalizationTests.cs
git commit -m "feat(api): symbol_blocked code + {code,message} normalization + snapshot aliases to unified envelope"
```

---

## Milestone M6 — Frontend Data-tab Jobs zone

Collapses the two localStorage job stores into one server-hydrated Jobs panel with uniform SSE, adds the Materialize button, and deletes the imperative forms. Depends on M2/M4 endpoints. **FE verification is `npm run lint && npm run test && npm run build`** — `frontend/package.json` has `"test": "vitest run"` (§B6, there IS a runner). **Every deleted `.tsx`/`.ts` has a companion `.test.*` that must be deleted or rewritten in the same task**, and deleting a store/hook breaks its importers — each task below lists them; a missed importer fails `npm run build`.

### Task M6.1: Main WebApi proxy routes for `/api/data/jobs*` + `/api/data/materialize`

**Files:**
- Modify: the main WebApi data-proxy route file (grep: `Grep "api/data/aggregations"` in `src/AlgoTradeForge.WebApi/` to find the existing proxy handler that forwards to HistoryLoader)
- Test: extend the existing proxy tests in `tests/AlgoTradeForge.WebApi.Tests/` if present

**Interfaces:**
- Produces: `/api/data/jobs`, `/api/data/jobs/{id}`, `/api/data/jobs/{id}/progress` (SSE pass-through, must **not** buffer — preserve `text/event-stream`), `/api/data/materialize`, `DELETE /api/data/jobs/{id}` all forwarding to HistoryLoader's `/api/v1/*`.

- [ ] **Step 1:** Locate the existing `/api/data/aggregations/{id}/progress` SSE proxy route (grep). Note how it disables response buffering for SSE and forwards `Last-Event-ID`.
- [ ] **Step 2: Add** the `jobs*` + `materialize` routes copying that exact pattern (the SSE route reuses the aggregation-progress streaming forwarder verbatim, only the path changes). Ensure `DataProxyCache` (if it caches GETs) does **not** cache `/jobs` (live data) or the SSE route.
- [ ] **Step 3: Build + smoke** — `dotnet build AlgoTradeForge.slnx`; then `curl` `/api/data/jobs` through the main WebApi and confirm it reaches HistoryLoader.
- [ ] **Step 4: Commit**

```bash
git commit -m "feat(webapi): proxy /api/data/jobs* + /api/data/materialize to HistoryLoader"
```

### Task M6.2: `data-api.ts` job methods + unified types + SSE endpoint switch

**Files:**
- Modify: `frontend/lib/services/data-api.ts`, `frontend/types/data-tab.ts`, `frontend/lib/services/data-sse-client.ts`
- Create: `frontend/hooks/use-jobs.ts`

**Interfaces:**
- Produces: `dataApi.getJobs()`, `dataApi.getJob(id)`, `dataApi.postMaterialize(body)`, `dataApi.deleteJob(id)`; `JobEnvelope` + `JobKind`/`JobState` TS types mirroring Shared Contracts; `connectProgress` points at `/api/data/jobs/{id}/progress`; `useJobs()` (`useQuery(["data","jobs"], …, { refetchInterval: 5000 })`).

- [ ] **Step 1:** Add `JobEnvelope`, `JobKind`, `JobState`, `MaterializeRequest` to `types/data-tab.ts` (snake_case wire fields; keep the existing `SseEventEnvelope` union — its `kind` values already match).
- [ ] **Step 2:** Add the four `dataApi` methods (`GET /jobs`, `GET /jobs/{id}`, `POST /materialize`, `DELETE /jobs/{id}`) following the existing `postLoad`/`getLoadJob` shape.
- [ ] **Step 3:** Change `connectProgress`'s URL from `/api/data/aggregations/${jobId}/progress` to `/api/data/jobs/${jobId}/progress` (Last-Event-ID header logic unchanged).
- [ ] **Step 4:** `use-jobs.ts` — `useJobs()` query hook.
- [ ] **Step 5:** Update the companion tests `frontend/lib/services/data-api.test.ts` (add cases for the four new methods) and `frontend/lib/services/data-sse-client.test.ts` (endpoint path changed to `/jobs/{id}/progress`) — both exist and will fail otherwise (§B6).
- [ ] **Step 6: Verify** — `cd frontend; npm run lint && npm run test && npm run build`. Expected: clean.
- [ ] **Step 7: Commit**

```bash
git add frontend/lib/services/data-api.ts frontend/lib/services/data-api.test.ts frontend/types/data-tab.ts frontend/lib/services/data-sse-client.ts frontend/lib/services/data-sse-client.test.ts frontend/hooks/use-jobs.ts
git commit -m "feat(fe): data-api job methods + unified JobEnvelope types + SSE endpoint switch"
```

### Task M6.3: Unified server-hydrated jobs store (kill localStorage as source of truth)

**Files:**
- Create: `frontend/lib/stores/jobs-store.ts` (only the `lastEventId` cache stays in localStorage; the job LIST comes from the server)
- Delete: `frontend/lib/stores/data-jobs-store.ts` (+ `data-jobs-store.test.ts`), `frontend/lib/stores/load-jobs-store.ts`, `frontend/hooks/use-load-job.ts`
- Modify (importers that will otherwise break the build — §B7): `frontend/components/features/launch/coverage-hint.tsx` (imports `useLoadJobsStore` AND the deleted `useLoadJob`), `frontend/components/features/data/coverage-summary.tsx` (`useLoadJobsStore`), `frontend/components/features/data/feed-status-card.tsx` (`data-jobs-store`)
- Modify: `frontend/components/features/data/use-job-stream.ts` (key by `jobId`, read `lastEventId` from the new store)

**Interfaces:**
- Produces: `useJobsStore` holding `{ [jobId]: { lastEventId } }` (localStorage, TTL-purged) — **only** the resume cursor, not the job identity. Job identity/list is `useJobs()` (server).

- [ ] **Step 1:** Create `jobs-store.ts` — a slim Zustand store persisted under `"atf-jobs-cursor"` with `{ [jobId]: { lastEventId, updatedAt } }`, `recordEvent(jobId, id)`, `purgeStale` (24h). No `setJob`/job-label state — the server list is authoritative.
- [ ] **Step 2:** Rewrite `use-job-stream.ts` to key by `jobId` (not the composite feed-job-key), reading/writing `lastEventId` from `useJobsStore`.
- [ ] **Step 3:** Delete the two old stores (+ `data-jobs-store.test.ts`) + `use-load-job.ts`; **rewire the three importers above** — `coverage-hint.tsx`/`coverage-summary.tsx` drop their load-job usage (the Jobs panel now shows load progress), `feed-status-card.tsx` drops its `data-jobs-store` dependency.
- [ ] **Step 4: Verify** — `npm run lint` (full `test`/`build` runs at the end of M6.4; **M6.3 + M6.4 land in ONE commit** so the build is never left red).
- [ ] **Step 5:** (commit with M6.4)

### Task M6.4: Unified `JobCard` + Jobs panel; delete the poll/SSE split

**Files:**
- Create: `frontend/components/features/data/job-card.tsx` (renders any `JobEnvelope` kind; materialize shows two-stage progress)
- Modify: `frontend/components/features/data/data-tab-root.tsx` (replace the "In progress" + "Archive loads" sections with one Jobs panel driven by `useJobs()`)
- Delete: `frontend/components/features/data/job-progress.tsx` (`JobProgressCard`) + `job-progress.test.tsx`; `frontend/components/features/data/load-job-card.tsx` (the actual `LoadJobCard` file, §B7) + `load-job-card.test.tsx`

**Interfaces:**
- Consumes: `useJobs()`, `JobEnvelope`, `use-job-stream.ts`.
- Produces: one `<JobsPanel>` listing all jobs from the server; each `<JobCard>` streams live via `use-job-stream` keyed by `job_id`; a cancel (✕) calls `dataApi.deleteJob`.

- [ ] **Step 1:** Build `job-card.tsx` — header (kind badge + feed_key + state chip), a progress bar from `progress.done/total`, `detail`-specific line (current month / bars / stage), cancel button for non-terminal jobs. Reuse the Tailwind tokens from `job-progress.tsx` (`bg-bg-surface`, `border-border-subtle`, `accent-*`).
- [ ] **Step 2:** In `data-tab-root.tsx`, replace both job sections with `<JobsPanel>` mapping `useJobs().data` → `<JobCard>`; remove `useDataJobsStore`/`useLoadJobsStore` usage and the `onJobAccepted` plumbing.
- [ ] **Step 3:** Delete `job-progress.tsx` + `load-job-card.tsx` and their `.test.tsx` siblings.
- [ ] **Step 4: Verify** — `npm run lint && npm run test && npm run build`. Expected: clean (all importers rewired in M6.3, all companion tests removed/rewritten).
- [ ] **Step 5: Commit** (M6.3 + M6.4 together)

```bash
git add frontend/lib/stores/jobs-store.ts frontend/components/features/data/job-card.tsx frontend/components/features/data/data-tab-root.tsx frontend/components/features/data/use-job-stream.ts frontend/components/features/launch/coverage-hint.tsx frontend/components/features/data/coverage-summary.tsx frontend/components/features/data/feed-status-card.tsx
git rm frontend/lib/stores/data-jobs-store.ts frontend/lib/stores/data-jobs-store.test.ts frontend/lib/stores/load-jobs-store.ts frontend/hooks/use-load-job.ts frontend/components/features/data/job-progress.tsx frontend/components/features/data/job-progress.test.tsx frontend/components/features/data/load-job-card.tsx frontend/components/features/data/load-job-card.test.tsx
git commit -m "feat(fe): unified server-hydrated Jobs panel + JobCard; kill localStorage job tracking + poll/SSE split"
```

### Task M6.5: Materialize button on grid cells; delete `ArchiveLoadForm` + `NewAggregateForm`

**Files:**
- Modify: `frontend/components/features/data/feed-cell.tsx` (the status-chip cell — the Materialize button lands here) and `frontend/components/features/data/asset-feed-grid.tsx` if the cell status is computed there; update `asset-feed-grid.test.tsx` if the cell output changes
- Modify: `frontend/components/features/data/data-tab-root.tsx` (remove the "Load archive data" button at ~line 86) + `frontend/components/features/data/data-sidebar.tsx` (~line 36 — drop the `ArchiveLoadForm`/`NewAggregateForm` sidebar modes)
- Delete: `archive-load-form.tsx` (+ `archive-load-form.test.tsx`), `new-aggregate-form.tsx` (+ `new-aggregate-form.test.tsx`)

**Interfaces:**
- Consumes: `dataApi.postMaterialize`, `useJobs` (to reflect the new job).
- Produces: a **Materialize** button on `declared`/`on-demand`-not-yet-materialized cells → `postMaterialize({exchange, symbol, feed})` → invalidate `["data","jobs"]` and the coverage query; the two imperative forms are gone.

- [ ] **Step 1:** Add a `useMutation` for `postMaterialize` (pattern from the old `new-aggregate-form.tsx`), wired to a Materialize button rendered on eligible `feed-cell.tsx` cells.
- [ ] **Step 2:** Remove the `ArchiveLoadForm`/`NewAggregateForm` sidebar modes + the "Load archive data" button; delete the two form files + their `.test.tsx` siblings + all imports (grep `ArchiveLoadForm`/`NewAggregateForm`).
- [ ] **Step 3: Verify** — `npm run lint && npm run test && npm run build`. Expected: clean.
- [ ] **Step 4: Manual smoke** — click Materialize on a declared derived feed → a materialize JobCard appears and streams both stages to complete.
- [ ] **Step 5: Commit**

```bash
git add frontend/components/features/data/feed-cell.tsx frontend/components/features/data/asset-feed-grid.tsx frontend/components/features/data/data-tab-root.tsx frontend/components/features/data/data-sidebar.tsx
git rm frontend/components/features/data/archive-load-form.tsx frontend/components/features/data/archive-load-form.test.tsx frontend/components/features/data/new-aggregate-form.tsx frontend/components/features/data/new-aggregate-form.test.tsx
git commit -m "feat(fe): Materialize button on grid cells; delete ArchiveLoadForm + NewAggregateForm"
```

---

## Live Smoke (post-implementation, before merge)

Run against `HistoryLoader__DataRoot=%LOCALAPPDATA%/AlgoTradeForge/HistoryTest` + isolated `ConfigRoot`, `ASPNETCORE_ENVIRONMENT=Development`, `ASPNETCORE_URLS=http://localhost:5051`, `--no-launch-profile`. Snake_case bodies. (The 3a lesson: live smoke catches restart bugs static review cannot.)

1. **Restart survives a running job** — start a load, kill the service mid-flight, restart → the row is `interrupted`, the reconciler resumes it, `GET /api/v1/jobs` shows it.
2. **SSE reconnect** — open `/api/v1/jobs/{id}/progress`, drop the connection, reconnect with `Last-Event-ID` → missed events redelivered from SQLite.
3. **Materialize composite** — `POST /api/v1/materialize` for a derived feed → both stages complete, composite progress advances end-to-end.
4. **OCE-filter grep** (final review, ALL layers): `Grep "catch when \(ex is not OperationCanceledException\)"` — confirm no new catch site introduced by this phase leaks HttpClient timeouts ([[feedback_oce_filter_pattern]]).

---

## Self-Review

- **Spec coverage:** §3.1 → M1 + M3.3/M3.4; §3.2 doorbell → M2.1/M2.4; §3.3 materialize → M4; §3.4 interrupted-sweep + AtomicReplace → M3.6/M3.7; §3.5 endpoints + envelope → M2.3/M2.4/M2.5 + M5; §3.6 FE → M6; F1 → M3.1; F2/F3 → M5; retention → M3.5; write gate/queued/UNIQUE/migration (R1–R3) → M1; dispatch/cancel/retention re-home (R2) → M3.3/M3.4/M3.5 + M2.5; envelope (R5) → M2.3.
- **Placeholder scan:** every code step carries real code or a named existing method to copy; FE steps name exact files.
- **Type consistency:** `IJobProgressSink`, `IHistoryIndex` additions, `JobEnvelope`, `IJobWakeupQueue`, `FeedGateOutcome`, `request_json` rehydration, 4-part `feed_key` are declared once in Shared Contracts and referenced by name throughout.

## Revision round (Fable plan review, 2026-07-12 — grounded against the code)

8 blockers + 9 should-fixes incorporated: **B1** index-before-column migration ordering (M1.1 — indexes created after ALTER); **B2** write-gate reentrancy self-deadlock via `PruneAssetsNotIn→RemoveAsset` (M1.2 — ungated `*Core` methods); **B3** durable row can't rebuild the work request (Shared Contracts + M3.3/M3.4 — `request_json` column + per-kind rehydrators); **B4** deletion blast radius (M3.3/M3.4 — exact DI files `DependencyInjection.cs:198`/`Program.cs:97`, `LoadJobProgress` real path, enumerated deletes, rewritten `LoadEndpointValidationTests`/`AggregationEndpointAssetResolutionTests`, kept the two agg queues); **B5** materialize stage feed-gate self-collision (M4.2 — stages do NOT re-claim; source concurrency handled by the 3a collector skip-gate); **B6** vitest exists + companion tests (M6 — gate is `lint && test && build`, every delete pairs its `.test.*`); **B7** store-deletion importers (M6.3 — `coverage-hint`/`coverage-summary`/`feed-status-card` rewired, `load-job-card.tsx` deleted); **B8** wrong storage-test path + `FileShare.Delete` (M3.7). Should-fixes: `queued`-birth ordering, doorbell no-op-when-cell-less (S2), `DeleteJob` rollback (S3), symbol-busy behavior change documented (S5), `MaxEventsPerJob` cap enforced in `AppendJobEvent` (S6), interrupted-materialize boot re-seed (S7), homeless DI registrations + sweep ordering (S8), exchange in `feed_key` (S9), cutoff string-compare format.
