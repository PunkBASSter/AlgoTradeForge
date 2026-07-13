# Persistent Jobs + Materialize + Frontend — Phase 3b Design

**Date:** 2026-07-12
**Status:** Approved design, pending implementation plan
**Parent spec:** `2026-07-10-declarative-data-management-design.md` (§3.3 jobs table, §3.4 startup sweep, §3.6 Jobs zone, §3.7 materialize endpoint)
**Predecessor:** `2026-07-11-group-driven-collection-phase3a-design.md` (§5 lists 3b's obligations)
**Scope:** HistoryLoader Application/Infrastructure/WebApi + trading frontend Data tab. Backend replaces both in-memory job registries with a SQLite-backed unified store; adds the `materialize` composite job; frontend gains a unified Jobs panel and Materialize button and drops the legacy imperative forms + localStorage job tracking.

## 1. Problem

Phase 3a moved collection onto the group-driven plan but left job state ephemeral. Three job mechanisms coexist with **different contracts**:

- **`LoadJobRegistry`** (`Application/Archive/Jobs/`) — in-memory. `Channel<LoadJob>` bounded queue; per-`feedKey` lock objects (`feedKey = {assetDir}|{feedName}|{interval}`); three indexes (`_byJobId`, `_activeByFeedKey`, `_activeByAssetDir`); lazy retention eviction; `LoadEnqueueOutcome` = Accepted / FeedBusy(423) / QueueFull. **Poll-only snapshot, no SSE, no cancellation.**
- **`AggregationJobRegistry`** + `AggregationJobRecord` (`Application/Aggregation/Jobs/`) — in-memory. Per-`feed-id` locks; **SSE event log** (`List<JobEvent>` + `NextEventSignal` TCS swap + `EventsAfter(seq)` + `LastSequence`); **cancellation** via `record.Cts` + `TryRequestCancel`; `MarkTerminal` populates Result/Error under the events lock before flipping `State` (capture-before-drain); `EnqueueOutcome` = Accepted / FeedAlreadyLocked(423) / QueueFull.
- **`index_jobs` table** (phase 1, in `history-index.sqlite`) — SQLite. Columns `id, kind, state, progress_json, error, created_at, updated_at`; API on `IHistoryIndex` (`CreateJob(kind)`, `UpdateJob(id, state, progressJson?, error?)`, `GetJob`, `GetActiveJob(kind)`, `GetLastJob(kind)`); `IndexJobRow(Id, Kind, State, ProgressJson, Error)`. Used **only** for index-rebuild/catalog jobs today. Startup sweep already present in `HistoryIndexInitializer.EnsureCreated`: `UPDATE index_jobs SET state='interrupted' WHERE state='running'`.

Consequences: everything vanishes on restart; the frontend papers over it with job ids in localStorage; there is no composite that runs the derived-feed chain (archive-load → resample) as one unit; and a `POST /loads` for a transient feed with an empty interval still throws inside the worker (same class as the phase-3a cadence bug).

The parent spec (§3.3) states `index_jobs` is meant to generalize into THE job store ("persistent replacement for both in-memory registries plus catalog-rebuild jobs"). This phase does that.

## 2. Decisions (agreed 2026-07-12)

| # | Decision | Choice |
|---|----------|--------|
| Q1 | Job-store scope | **Generalize `index_jobs`** into the unified store (`kind ∈ {index, load, aggregation, materialize}`) + a child `job_events` table. One store, not a parallel `jobs` table. Reuses the existing startup interrupted-sweep. |
| Q2 | SSE-from-SQLite | **`job_events` is the single source of truth**; live SSE liveness comes from a content-free, in-process **per-job doorbell** (signal-only, no event state in memory). Legacy in-memory event log dies fully — no write-through ([[feedback_kill_legacy_over_adapters]]). Last-Event-ID replay = `SELECT WHERE seq > ?`. |
| Q3 | Materialize shape | **Single resumable row** `kind=materialize` with sub-stages in `progress_json`; load/aggregate work extracted into callable services shared by standalone job kinds. On-demand-collected feed = one-stage materialize (not a degenerate parent). Not parent-with-child-jobs. |
| Q4 | Interrupted-sweep | **Hybrid:** the job row records touched `(feed_key, month-range)`; boot flips `running→interrupted` then targeted drift-rescan of those feeds/months **before** boot convergence; the ordinary 3a kick re-collects. Wholesale/atomic-rename month writes are the safety precondition — **already satisfied** by both writers (§3.4). |
| Q5 | FE Jobs panel | **One unified panel, uniform SSE for all kinds.** Load now emits `job_events` like everyone else; one rendering path, one reconnect path; the poll/SSE split (an artifact of two registries) is removed. |
| F1 | Load transient-feed interval guard | **Fold in.** Guard `IntervalParser.ToTimeSpan("")` in the load worker's transient-feed branch (FeedCadence.DiskInterval fallback), same class as the 3a cadence merge-blocker. |
| F2 | `symbol_blocked` error code | **Fold in.** Blocked assets (unknown precision, excluded from plan) currently return `422 symbol_not_declared`, which lies — they ARE declared. Distinct `symbol_blocked` code/message. |
| F3 | Error-body consistency (P5) | **Fold in.** Normalize `/loads`, backfill, aggregation, status endpoints onto a single `{code, message}` envelope. |
| F4 | Delete orphaned collector feeds | **Deferred** (parent spec keeps this a separate deferral; today's delete covers alt-bars only). |
| F5 | Delisting end-of-life dates | **Deferred** (3a's kick fingerprint already keeps permanently-partial tuples from looping). |

**Review-round refinements (Opus review, 2026-07-12 — grounded against the code).** The SQLite concurrency model and post-registry mechanics were under-specified in the first draft; nailed down here: (R1) a **process-wide index write gate** + `queued` initial state + a **UNIQUE partial index** make the feed-gate an atomic claim rather than a racy check-then-claim (§3.1); (R2) deleting the registries deletes the **work-dispatch channel, cancellation delivery, and retention** — all three are explicitly re-homed, not assumed (§3.1); (R3) the schema migration is **version-guarded** because `ALTER TABLE ADD COLUMN` throws on re-run (§3.1); (R4) the "atomic write, torn file impossible" claim was **false for the streaming writer** (`LocalFileStorage.AtomicReplace` = delete-then-move) — the interrupted-sweep handles missing-file/orphan-tmp and `SetTouched` is written **before** the fetch, plus a Storage-layer task makes `AtomicReplace` truly atomic (§3.4); (R5) the **unified job wire envelope** is defined concretely, not deferred (§3.5).

## 3. Design

### 3.1 Unified job store — `index_jobs` generalized + `job_events`

`index_jobs` becomes the single durable job store.

**Concurrency substrate (this is load-bearing — the whole phase's correctness rests on it).** Today `SqliteHistoryIndex.Open` news a fresh `SqliteConnection` per operation with no `busy_timeout` and no write gate, and `CreateJob` inserts `state='running'` immediately (`SqliteHistoryIndex.cs:393`). That is fine for the single-writer catalog-rebuild job, but 3b has many workers appending progress and claiming feed-gates concurrently across independent connections — with `busy_timeout=0` the second concurrent writer gets `SQLITE_BUSY` (throw), not serialization. Two fixes, both required:

1. **Process-wide index write gate.** A single `SemaphoreSlim(1,1)` in `SqliteHistoryIndex`, acquired via `SemaphoreSlimExtensions.LockAsync` (Constitution v1.9.1), wraps every *write* op (job create/update, event append, feed-gate claim, month upserts). HistoryLoader is a single host, so an in-process gate is sufficient and simplest; it also makes the seq-allocation and gate-claim below atomic without relying on SQLite-level locking. Reads stay ungated. Add `PRAGMA busy_timeout=5000` on `Open` as defense-in-depth for the WAL reader/checkpoint path.
2. **`queued` initial state.** `CreateJob` inserts `state='queued'`, not `running`; the worker flips `queued→running` at dequeue. This disambiguates the startup sweep (only `running` = actually-interrupted mid-execution) from never-started rows, and gives the feed-gate a single predicate (`state IN ('queued','running')` = active).

Additive, **guarded** schema migration — `HistoryIndexInitializer.EnsureCreated` currently only runs one `CREATE TABLE IF NOT EXISTS` blob and never reads/compares `schema_version`. It gains a real version step: read the stored version; if `< 2`, run the following inside a transaction, then `UPDATE schema_version SET version=2`. `ALTER TABLE ADD COLUMN` has **no `IF NOT EXISTS`** and throws *"duplicate column name"* on re-run, so it MUST be version-guarded (not idempotent on its own):

```sql
-- Run once, guarded by schema_version < 2:
ALTER TABLE index_jobs ADD COLUMN feed_key         TEXT NULL;   -- busy-gate key; NULL for index/catalog jobs
ALTER TABLE index_jobs ADD COLUMN cancel_requested INTEGER NOT NULL DEFAULT 0;
ALTER TABLE index_jobs ADD COLUMN touched_json     TEXT NOT NULL DEFAULT '[]'; -- [{feedKey, month}] for interrupted-sweep

CREATE TABLE IF NOT EXISTS job_events (
    job_id       TEXT    NOT NULL,
    seq          INTEGER NOT NULL,
    kind         TEXT    NOT NULL,          -- queued|started|progress|complete|error|cancelled
    payload_json TEXT    NOT NULL,
    created_at   TEXT    NOT NULL,
    PRIMARY KEY (job_id, seq)
);

CREATE INDEX IF NOT EXISTS ix_jobs_kind_state ON index_jobs(kind, state);
-- UNIQUE partial index: enforces the feed-gate at the DB level so a losing concurrent claim
-- fails deterministically instead of silently double-acquiring.
CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active_feedkey
    ON index_jobs(feed_key) WHERE feed_key IS NOT NULL AND state IN ('queued','running');
```

`index_jobs` holds only transient catalog-rebuild jobs today, so no historical rows carry semantics worth preserving across the migration. `job_events` is not disk-derived (operational state, not rebuildable from DataRoot), so it is exempt from the "index is rebuildable" principle — it lives in `history-index.sqlite` for locality but is authoritative for its own rows. The startup sweep must run **after** the migration and now targets only `state='running'` (queued rows that never started are re-dispatched, not marked interrupted).

**`IHistoryIndex` job API extensions** (drop the `Async` suffix per convention; all `CancellationToken ct = default`):

- `AppendJobEvent(jobId, kind, payloadJson)` → `int seq`. Allocates the next `seq` for the job **atomically** (`INSERT ... SELECT COALESCE(MAX(seq),0)+1` under the write transaction) and inserts the row. Returns the assigned seq.
- `GetJobEventsAfter(jobId, afterSeq)` → `IReadOnlyList<JobEventRow>` (`seq > afterSeq`, ordered).
- `GetLastEventSeq(jobId)` → `int`.
- `TryAcquireFeedGate(kind, feedKey, touched)` → `FeedGateOutcome` (Acquired(jobId) / Busy(existingJobId)). **Atomic claim, not check-then-claim:** the job row is created and the gate is claimed in one guarded statement under the write gate — `INSERT INTO index_jobs (...) SELECT ... WHERE NOT EXISTS (SELECT 1 FROM index_jobs WHERE feed_key=$fk AND state IN ('queued','running'))`, and the `ux_jobs_active_feedkey` UNIQUE partial index is the second line of defense (a racing insert that slips past the WHERE-NOT-EXISTS fails the UNIQUE constraint deterministically). On conflict, `SELECT` the current owner and return `Busy`. Released implicitly when the owning job reaches a terminal state — the gate is the `feed_key`+`state` predicate itself, no separate lock table. This replaces the old check-then-claim `LoadJobRegistry.TryEnqueue` (which was safe only because a per-`feedKey` `lock` object serialized it in-process); the durable equivalent needs the UNIQUE index because two requests can be on two connections.

**One canonical `feed_key` grammar across all kinds.** The two dead registries keyed differently (load: `{assetDir}|{feedName}|{interval}`; aggregation: the output feed-id). The unified gate uses **one** convention — the load grammar `{assetDir}|{feedName}|{interval}` — for every kind, keyed on the feed the stage *touches*: a load/collection touches its source feed, an aggregation touches its **output** (derived) feed, and a materialize touches its source feed in the load stage and its output feed in the aggregate stage. This makes contention correct: a manual aggregation of feed `X` and a materialize whose aggregate-stage produces `X` collide on the same key; a load of source `S` and the load-stage of a materialize that reads `S` collide on `S`; a load and an unrelated aggregation never collide. The plan maps the existing feed-id to this grammar.
- `RequestCancel(jobId)` → sets the durable `cancel_requested=1`. This is only the durable record of intent; *delivery* is the in-process CTS map (§3.1 "Cancellation delivery") — the `DELETE` handler sets this flag and trips the live CTS in the same call. A resumed job re-checks the flag at its next progress checkpoint.
- `SetTouched(jobId, feedKey, month)` — records the single **in-flight** `(feedKey, month)` the worker is *about to* collect, written **before** the fetch (§3.4). Overwrites as the worker advances month to month; the interrupted-sweep reads the last value.
- `ListInterruptedJobs()` → jobs in `state='interrupted'` with their `touched_json` (boot consumer).

**Two creation paths, disjoint by gate:** gated kinds (`load`/`aggregation`/`materialize`) are born through `TryAcquireFeedGate`, which inserts the `queued` row and claims the gate atomically (above). `CreateJob(kind)` stays for **gateless** kinds only (`index`/catalog rebuild — `feed_key IS NULL`). `UpdateJob` stays for state/progress/error transitions. `IndexJobRow` gains `FeedKey`, `CancelRequested`, `Touched`.

**Both in-memory registries are deleted**, not adapted — but three things they did are NOT subsumed by "durable row + worker concurrency bound" and must be explicitly re-homed, or they silently regress:

1. **Work dispatch / wakeup.** The bounded `Channel<LoadJob>` (`LoadJobRegistry.cs:29`) and the aggregation `Channel` are not just concurrency bounds — they are how a worker *learns a new job exists* (`LoadJobWorker` is a single consumer on `Dequeue`; `AggregationWorkerHost` fans N workers over `ReadAllAsync`). The per-job doorbell (§3.2) is per-job and cannot announce *new* work. So each worker host keeps an **ephemeral in-process `Channel<string jobId>` wakeup queue**: the durable `index_jobs` row is the source of truth, the channel only carries "job N is ready, come pick it up." A create endpoint writes the row (under the write gate + feed-gate) then `TryWrite(jobId)` to the host's channel. On boot, the host seeds its channel from `state='queued'` rows (crash-safe: the durable row outlives the ephemeral channel). `MaxQueueDepth`/`QueueFull` backpressure (today → the enqueue outcome) is re-homed as the channel's bound; a full channel returns `QueueFull` (503) exactly as today.
2. **Cancellation delivery.** A durable `cancel_requested=1` flag does not abort an in-flight `HttpClient`/IO call — only a tripped `CancellationToken` does. So a small **in-process `ConcurrentDictionary<jobId, CancellationTokenSource>`** for *running* jobs survives (it is a cancellation-delivery map, not the deleted registry). `DELETE /jobs/{id}` (in-process) both sets the durable flag AND trips the live CTS for instant cancel; a resumed job on another process instance checks the durable flag at its next progress checkpoint. The CTS is linked to the host's stopping token and disposed on terminal.
3. **Retention / pruning.** Both registries did lazy terminal-retention eviction; deleting them means `index_jobs` and `job_events` grow forever and `GET /jobs` grows unbounded. A **`JobRetentionSweeper`** (periodic, cheap) deletes terminal jobs (+ their `job_events` via `ON DELETE CASCADE` or an explicit child delete) older than `HistoryLoaderOptions.Jobs.RetentionMinutes` (default mirrors today's `Load.JobRetentionMinutes`), and caps `job_events` per job (progress events are the bulk — keep the last K + all non-progress lifecycle events).

Parallelism still comes from the worker hosts' own bounds (`MaxBackfillConcurrency`, aggregation slots); lifecycle from `state` transitions; busy-rejection from the durable feed-gate. The proven per-key-lock / terminal-eviction concurrency is replaced by SQL predicates + the three re-homed mechanisms above; regression risk is covered by the contract + concurrency tests in §4, not by compat shims.

**A third worker host executes `materialize` jobs** — `MaterializeWorkerHost`, structured like the existing load/aggregation hosts (its own wakeup channel + concurrency bound), invoking the extracted services (§3.3) stage by stage.

### 3.2 SSE from SQLite — the doorbell

`job_events` is authoritative. Liveness is a **content-free, in-process doorbell**: `IJobEventSignal` holds a per-job awaitable (a swapped `TaskCompletionSource`, mirroring `AggregationJobRecord.NextEventSignal`, but carrying **no event payload** — only "job X changed"). It is a singleton in the HistoryLoader host; all writers are in-process, so no cross-process notification is needed.

**Doorbell lifecycle** (or a wakeup is lost / the dict leaks): the per-job cell is `GetOrAdd`'d lazily by both reader and writer (a reader that arrives before the first event must create the cell so the first `Signal` finds it; otherwise the wakeup is lost and the reader hangs until the next event). `Signal(jobId)` swaps in a fresh TCS and completes the previous under `RunContinuationsAsynchronously` (same `Interlocked.Exchange` pattern as `AppendEvent`). Cells are **evicted when the job reaches a terminal state** (after the terminal event is appended and signalled) — bounded lifetime, no growth keyed by every jobId ever seen. A late reader of an already-terminal job finds no cell, reads the durable tail from `job_events`, sees the terminal event, and closes without ever awaiting.

Write path (preserving capture-before-drain ordering):

1. Worker calls `AppendJobEvent(jobId, kind, payload)` — durable insert, returns `seq`.
2. Worker then pulses `IJobEventSignal.Signal(jobId)` — swaps in a fresh TCS and completes the previous one.

SSE endpoint (`GET /jobs/{jobId}/progress`, generalizing today's `GET /aggregations/{jobId}/progress`):

1. Parse `Last-Event-ID` header → `lastSentSeq` (0 if absent).
2. Loop while not aborted:
   - Capture `nextSignal = IJobEventSignal.Next(jobId)` **before** the read (capture-before-drain — an event appended between capture and read has already completed the captured signal, so the next iteration drains it; capture-after-drain would TOCTOU and lose terminal events).
   - `fresh = GetJobEventsAfter(jobId, lastSentSeq)`.
   - If `lastSentSeq == lastEventId && lastEventId > 0 && fresh.Count == 0 && GetLastEventSeq > 0`: replay from 0 (resume past a last-known id whose exact seq we no longer hold — same guard as today).
   - Write each event as an SSE frame `id: {seq}\nevent: {kind}\ndata: {payload}\n\n`; advance `lastSentSeq`; on a terminal event (`complete|error|cancelled`) return.
   - `await nextSignal`.
3. `410 Gone` if the job id is unknown (no row and no events) — same as today's expired-record behavior.

Because the endpoint reads events from SQLite by `seq`, Last-Event-ID resume and full replay both work across a **service restart**: a reconnecting client gets the durable tail regardless of doorbell state (the doorbell is process-lifetime and correctly starts empty after a restart — the first read drains the durable backlog immediately, then blocks on the fresh signal).

**Terminal-event durability under kill-9:** the terminal event is a normal `job_events` row inserted before the doorbell pulse; if the process dies after the insert, a reconnecting client still receives it. If the process dies before the terminal insert (job was mid-flight), the boot sweep marks the row `interrupted` and the client's reconnect surfaces `interrupted` state — never a silently-hung stream.

### 3.3 Materialize composite — single resumable row

`POST /api/v1/materialize` (body: exchange, symbol, feed, optional date range) resolves the target against the plan/index and creates one job `kind=materialize`. `progress_json` carries the composite shape:

```jsonc
{ "stage": "load" | "aggregate", "stageIndex": 0, "stagesTotal": 2,
  "stageProgress": { "currentMonth": "2024-03", "monthsDone": 3, "monthsTotal": 12 } }
```

- **Derived feed** (e.g. `EqV_1k` from `agg-trades`, or `candles_5m` from `candles`): two stages — archive-load the source, then aggregate/resample. `stagesTotal = 2`.
- **On-demand-collected feed** (e.g. `agg-trades` itself): one stage — archive-load. `stagesTotal = 1`. Not a degenerate parent; just a one-stage plan.

**Work extraction.** The execution bodies of the load worker and the aggregation worker are pulled into callable services:

- `IArchiveLoadService.Run(request, IJobProgressSink sink, ct)` — one feed's archive backfill (wraps `ArchiveBackfillService.CoverFromArchive`).
- `IAggregationService.Run(request, IJobProgressSink sink, ct)` — one derived feed's resample (wraps the aggregation worker body).

`IJobProgressSink` is the single seam onto the store: `Report(progressJson)` → `UpdateJob` + a `progress` `job_events` row + doorbell pulse; `Complete/Fail/Cancel` → terminal event. Standalone `kind=load` and `kind=aggregation` jobs invoke the same services with a sink that owns the whole job; the `materialize` worker invokes them in sequence with a sink that maps each stage's progress into the composite `progress_json` and appends stage-scoped events. The job **is** a durable envelope around one or more service invocations — no orchestration lives in the services themselves.

**Busy-gate at feed-key level.** Each stage acquires the durable feed-gate for the feed it touches via `TryAcquireFeedGate`. A materialize load-stage therefore serializes correctly against a concurrent manual `POST /loads` of the same feed (both contend on the same `feed_key`), and its aggregate-stage against a concurrent manual aggregation — the gate lives on the feed, not the job.

**Resume.** On restart an interrupted `materialize` row resumes from `stageIndex`: a completed load-stage is not redone (its feed's months are complete in the index → complete-month-skip makes re-entry cheap even if re-run); an interrupted stage re-runs from its last complete month (§3.4). Every stage is idempotent, so resume-from-stage is safe.

### 3.4 Interrupted-sweep — recover the in-flight month, then the 3a kick

The startup `UPDATE index_jobs SET state='interrupted' WHERE state='running'` stays (now running after the migration, keyed on `running` only — `queued` rows are re-dispatched, §3.1). What is new is reconciliation of the interrupted job's in-flight work against disk **before** boot convergence.

**Write atomicity is asymmetric — the sweep must account for it.** The two month writers do NOT have the same crash semantics:

- `PartitionFileWriter.ReplacePartition` (archive-load path) writes `.tmp-{guid}` then `File.Move(overwrite: true)` — **atomic**; a crash leaves old-or-new, never torn.
- `BufferedPartitionWriter.FlushPartitionAsync` (streaming/scheduled-collector path) publishes through `IFileStorage.WriteAllLines` → `LocalFileStorage.AtomicReplace`, which is **`File.Delete(dst)` then `File.Move(src, dst, overwrite:false)`** (`LocalFileStorage.cs:237`, with an acknowledged "brief window where dst is absent" comment). A crash *between* Delete and Move leaves the month file **absent** with an orphan `.tmp` — while the `month_partitions` row still points at a month that no longer exists on disk.

So the crash outcome set is **old / new / missing-with-orphan-tmp**, not a clean binary. Two consequences the design must own:

1. **Plan task (Storage layer): make `AtomicReplace` truly atomic** — switch to `File.Move(src, dst, overwrite:true)` (MoveFileEx / rename; already proven safe with open readers by `PartitionFileWriter`, which uses `overwrite:true` + a one-shot IOException retry). This closes the missing-file window for *all* callers, not just collectors. It is a shared method (CAS writes, feed-schema writes also call it), so it rides as its own task with its own regression tests, but it is in-scope because 3b's durability story depends on it.
2. **The sweep tolerates the window regardless** — even after the fix, a mid-`WriteAllLines` crash can leave the month with fewer rows than intended (the incomplete-month hazard below), so the sweep is written to handle absent-file + orphan-tmp defensively.

**Why a naive "targeted drift-rescan" does not detect the real hazard.** The genuine interrupted-job hazard is an **incomplete month**: the job died after writing *some* of the month's rows. But a drift-rescan compares `file_len`/`file_mtime`/`rows` *vs disk* — if the worker already upserted the index row from that same partial file, disk == index and the rescan flips nothing. Incompleteness is only detectable against the **expected** full-month row count, which is 3a's existing completeness math (`MonthCoverageMath` / `complete_months_json`), not a drift comparison. This splits by feed kind:

- **Row-count-bearing feeds** (candles, most aux feeds): 3a's boot convergence already recomputes completeness and flips a short month to `partial`, and the 3a kick re-collects it — **no special sweep action needed**; the durable job just needs to not block that path.
- **Feeds where row-math cannot run** (funding-rate, ticks, stream feeds — no fixed expected-per-month): completeness math is blind, so the *only* signal that the in-flight month may be short is the job's own record of what it was doing. This is what `touched_json` is for.

**`SetTouched` is written before the fetch, not after.** The worker persists `{feedKey, month}` for the month it is *about to* collect **before** issuing the fetch/write — so if it dies mid-month, the interrupted row names exactly the possibly-incomplete month. (Recording it only after completion would leave the in-flight month invisible — the exact hole finding-6 flags.) It is a single tiny upsert per month boundary, cheap.

**Sweep mechanism.** `InterruptedJobSweeper` (boot, after migration + startup sweep, before the reconciler's first convergence):

1. `ListInterruptedJobs()` → each interrupted job's `touched_json` in-flight `(feedKey, month)`.
2. For each, reconcile that specific month against disk: if the file is **absent/orphan-tmp**, delete the stale `month_partitions` row (and any orphan `.tmp`) so convergence sees the month as missing; otherwise **invalidate the month's completeness** (clear it from `complete_months_json` / force a re-scan of that one month) so it cannot be read as complete on the strength of a pre-crash row.
3. Hand off to the ordinary 3a pipeline: the first boot convergence sees the month as missing/partial and the kick re-collects from there. Watermark dedup + wholesale re-publish make re-collection duplicate-free; complete-month-skip keeps the rest cheap.

The sweeper makes coverage truthful for the interrupted in-flight month; it does **not** itself re-enqueue — the 3a kick remains the single re-enqueue path (one owner, no second debouncer, per 3a §3.1). An interrupted `materialize` job additionally resets to its last completed `stageIndex` (§3.3) so its stages resume rather than restart.

### 3.5 API surface

Generalize the aggregation-specific SSE/lifecycle endpoints to a job-kind-neutral surface, keeping the existing kind-specific create endpoints:

- `POST /api/v1/materialize` — **new** composite job. Returns `202` + `{ job_id, location: /api/v1/jobs/{id}/progress }`.
- `GET /api/v1/jobs` — list jobs (filter by `kind`/`state`), for the unified panel.
- `GET /api/v1/jobs/{jobId}` — snapshot (replaces `GET /loads/{id}` and `GET /aggregations/{id}`; the old paths may 308-redirect or stay as thin aliases for one release — **decide in plan**, default: keep aliases, single handler).
- `GET /api/v1/jobs/{jobId}/progress` — **uniform SSE** for all kinds (generalizes `/aggregations/{id}/progress`).
- `DELETE /api/v1/jobs/{jobId}` — request cancel (`RequestCancel`); generalizes `DELETE /aggregations/{id}`.
- `POST /loads`, `POST /exchanges/{exchange}/assets/{asset}/aggregate` — unchanged creators, now writing to the durable store and emitting `job_events`. `POST /loads` gains the F1 interval guard and F2 `symbol_blocked` code.

Error bodies normalized to `{ code, message }` (F3) across `/loads`, backfill, aggregation, status.

**Unified job wire envelope.** `GET /jobs` and `GET /jobs/{id}` return one shape across all kinds; the kind-specific payload is a discriminated `detail` object so the FE has one parse path (this is a contract, decided here, not deferred):

```jsonc
{
  "job_id": "…", "kind": "load|aggregation|materialize|index",
  "state": "queued|running|complete|error|cancelled|interrupted",
  "feed_key": "BTCUSDT_perp|candles|1m",        // null for index jobs
  "created_at": "…", "updated_at": "…",
  "error": { "code": "…", "message": "…" } | null,
  "progress": {                                  // common, kind-agnostic
    "phase": "…",           // human label: current stage/partition
    "done": 3, "total": 12, // unit-agnostic counters (months, stages, partitions)
    "detail": { … }         // kind-specific, discriminated by `kind`:
      // load:        { "current_month": "2024-03", "months_done": 3, "months_total": 12 }
      // aggregation: { "current_partition": "2024-03", "bars_emitted": 12345, "output_feed_id": "…" }
      // materialize: { "stage": "aggregate", "stage_index": 1, "stages_total": 2, "stage_progress": { … } }
  }
}
```

SSE `progress` events carry the same `progress` object; terminal `complete`/`error`/`cancelled` events carry the `error`/result payload. The old `GET /loads/{id}` and `GET /aggregations/{id}` snapshot shapes are reconciled onto this envelope now (their kind-specific fields become `detail`); the alias endpoints (§3.5) return the unified shape.

### 3.6 Frontend — Data tab Jobs zone

The Jobs zone (parent spec §3.6 zone 3) reads `GET /api/v1/jobs` and streams every kind through one `GET /api/v1/jobs/{id}/progress` EventSource with Last-Event-ID reconnect. One `JobCard` component renders load / aggregation / materialize / index uniformly from the shared event shape; materialize cards show the two-stage progress from `progress_json`.

- **Materialize button** on Explorer cells with status `declared`/`on-demand` not-yet-materialized → `POST /api/v1/materialize`, then follow the returned progress stream.
- **Delete `ArchiveLoadForm` and `NewAggregateForm`** — declarations + Materialize replace them.
- **Kill localStorage job tracking** — the durable `GET /api/v1/jobs` list is the source of truth; on load the panel hydrates from the server, so a refresh or a new browser sees in-flight and recent jobs.

## 4. Testing

- **Migration** (contract): a v1 `history-index.sqlite` fixture migrates to v2 once; a **second `EnsureCreated` on the migrated DB does not throw** (the `ALTER TABLE ADD COLUMN`-on-re-run trap); `schema_version` ends at 2; existing catalog-rebuild rows survive.
- **Write gate + atomic feed-claim** (concurrency): N concurrent `TryAcquireFeedGate` for the same `feed_key` → exactly one `Acquired`, the rest `Busy(sameOwner)` (drives the `ux_jobs_active_feedkey` UNIQUE index + write gate); different feed_keys → all acquire; gate frees on terminal; `queued` rows count as active for the gate.
- **Job-store contract** (`SqliteHistoryIndexTests` pattern: `Pooling=False`, `ClearAllPools` in Dispose): job CRUD with `feed_key`/`cancel_requested`/`touched_json`; `AppendJobEvent` monotonic seq under concurrent appends **without `SQLITE_BUSY`** (proves the write gate serializes); `GetJobEventsAfter`; `GetLastEventSeq`; `ListInterruptedJobs` returns `touched_json`.
- **Dispatch wakeup**: a create writes the durable row then wakes the host channel; on **boot the host seeds its channel from `state='queued'` rows** (a job enqueued before a restart still runs); `QueueFull` returned when the channel bound is hit.
- **Cancellation**: `DELETE /jobs/{id}` sets `cancel_requested=1` AND trips the in-process CTS → in-flight job stops promptly; a job with `cancel_requested=1` discovered on boot stops at its next checkpoint without running to completion.
- **Retention**: `JobRetentionSweeper` deletes terminal jobs + their `job_events` past the window; `job_events` per-job cap keeps lifecycle events and trims progress; active jobs untouched.
- **Doorbell**: capture-before-drain — an event appended between signal-capture and read is delivered (no lost terminal event); reader that arrives before the first `Signal` still wakes (lazy `GetOrAdd` cell); a terminal event closes the stream exactly once; cell evicted on terminal; **many concurrent readers** of one job all receive every event; Last-Event-ID resume returns only `seq > id`; replay-past-last-known guard fires.
- **SSE across restart** (integration): append events, drop the doorbell (simulate restart), a fresh reader with `Last-Event-ID` drains the durable tail from SQLite; a job left `running` at crash surfaces as `interrupted` to the reconnecting client, never a hung stream.
- **Materialize**: two-stage derived job reports `stage` transitions and completes both; one-stage on-demand job; resume-from-`stageIndex` after a simulated interruption skips the completed load stage.
- **Interrupted-sweep** (both hazards): (a) row-bearing feed with a short last month → boot marks `interrupted`, 3a completeness math flips it `partial`, kick re-collects exactly once, second convergence enqueues nothing (idempotency); (b) **missing-file/orphan-tmp** — `month_partitions` row present but the file was left absent by a mid-`AtomicReplace` crash → sweep deletes the stale row + orphan `.tmp`, convergence re-collects the month; (c) row-math-blind feed (funding/ticks) with `touched_json` naming the in-flight month → that month is re-collected even though completeness math can't judge it; `SetTouched`-before-fetch means the in-flight month is never invisible.
- **`AtomicReplace` atomicity** (Storage layer, its own task): after the `overwrite:true` switch, a concurrent reader holding the file open does not break the replace (mirrors `PartitionFileWriter`'s proven retry); no delete-then-move window remains. Regression tests for the other callers (CAS writes, feed-schema writes).
- **Unified envelope**: `GET /jobs/{id}` for each kind serializes the common shape with the correct discriminated `detail`; the old `/loads/{id}` and `/aggregations/{id}` aliases return the unified envelope.
- **Feed-gate correctness (endpoint)**: a materialize load-stage and a concurrent manual `POST /loads` of the same feed → one 423; different feeds → both proceed.
- **F1/F2/F3**: transient feed with `interval:""` does not throw (guarded); blocked asset returns `symbol_blocked` not `symbol_not_declared`; error bodies are `{code, message}` across all four endpoints.
- **OCE-filter sweep** (final-review hygiene, all layers): grep for `catch when (ex is not OperationCanceledException)`. Confirmed currently **clean** in `src/` (workers already use `IsTrueShutdown`/token-checked filters) — the sweep guards against a regression introduced by this phase's new catch sites, not an existing bug ([[feedback_oce_filter_pattern]]).

**Live smoke** (the 3a lesson: live smoke catches restart bugs static review cannot). Exercise, on `HistoryTest` DataRoot + isolated ConfigRoot:

1. Start a load/aggregation, restart the service mid-flight → the row is `interrupted`, the reconciler resumes it, and the durable job list shows it.
2. Open the SSE stream, reconnect with `Last-Event-ID` after dropping the connection → missed events are redelivered from SQLite.
3. Run a `POST /materialize` for a derived feed → both stages complete and the composite progress advances end-to-end.

## 5. Out of scope / deferred

- **Parquet** (`ParquetFeedFormat`) — phase 4.
- **Backtest-launch auto-materialize** — later phase on the same `/materialize` endpoint.
- **Explicit deletion of orphaned collector-managed feeds** — today's `DELETE .../feeds/{id}` covers alt-bars only (F4).
- **Delisting end-of-life dates** — symbology/index field capping `expected` at `min(now, delistMonth)`; the 3a kick fingerprint keeps permanently-partial tuples from looping without it (F5).
- **Cross-process job notification** — the doorbell is in-process; HistoryLoader is a single host. A multi-host future would replace the doorbell with a durable notification channel, but the SQLite `job_events` tail already makes that a drop-in (the endpoint would poll or subscribe instead of awaiting the in-process signal).
