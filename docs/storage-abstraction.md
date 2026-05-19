# Storage Abstraction: Local FS + S3 backends for HistoryLoader and Backtest Engine

> **Status (2026-05-18):** PR 1–3 complete; PR 4a complete; PR 4a.1 complete; PR 4b complete; PR 4c complete; PR 5 complete. PR 3 introduced `BufferedPartitionWriter` + `BufferedWriterFlushService` and migrated the four HistoryLoader CSV writers (candle, feed, daily-tick, daily-book-ticker) to buffer-then-PUT atop `IFileStorage`. Torn-row recovery deleted. PR 4a routed `IFeedStatusStore` + `ISettingsWriter` through `IFileStorage` (async, no `Async` suffix), bound `AppSettingsWriter` to `LocalFileStorage` directly, and hardened `LocalWriteSession.Commit` with `Flush(flushToDisk: true)`. PR 4a.1 converted `ISchemaManager` (8 methods), `IFeedCatalog` (5 methods), and `AggregationPipeline.Run` to async; swapped `ReaderWriterLockSlim` for per-asset `SemaphoreSlim` in `FeedSchemaManager`; routed manifest I/O through `IFileStorage`; propagated `Task`/`await` through the catalog/aggregation endpoint handlers, `FundingInfoRefreshService`, `AggregationWorkerHost`, the three stream services, the feed collectors, `AggregatedDirSweeper`/`StartupSweepService`, and the benchmark harness. PR 4b migrated `PartitionedSourceReader` (now async + `IFileStorage.ListKeys`/`OpenRead`, returns `IAsyncEnumerable<SourceRecord>`), `PartitionedSinkWriter` (now `IAsyncDisposable` with static `Open` factory and explicit `Complete`; per-partition `IObjectWriteSession`; first overflow commits speculative bare key then `Move`s to `.p01`, cross-month sticky pre-opens every subsequent partition at its final key), `OverwritePathWriter` (per-key `Move` + orphan-prune + `DeleteByPrefix` replaces the atomic dir-rename-aside on local FS), `CandleExtJoiningSource` (async iterator + `IAsyncDisposable` cursor), `MonotonicTickSource` (added `IAsyncEnumerable` overload alongside the sync one), `AggregatedDirSweeper` + `StartupSweepService` (derive "immediate subdirs" from key prefixes via `ListKeys`/`DeleteByPrefix`), and `AggregationPipeline.Run` (chains all of the above through `await foreach`). Deleted orphan `SameVolumeGuard` and `CrossVolumeGuardTests` — the volume-guard responsibility migrated to `IFileStorage` boundary semantics. PR 4c promoted `IRunSink` to `IAsyncDisposable`, added `FlushAsync` to the interface, and rewrote `JsonlFileSink` to buffer events in a `MemoryStream` and atomically re-publish the whole file via `IFileStorage.WriteAllBytes` (same buffer-then-PUT shape as `BufferedPartitionWriter`). Live in-place file tailing of `events.jsonl` is replaced by the existing WebSocket sink for live consumers; on-disk consumers (`SqliteEventIndexBuilder`, `SqliteTradeDbWriter`) run after sink disposal so the final flush makes the stream visible before they touch it. `SimulationCacheFileStore` migrated to async with `OpenWriteSession`/`OpenRead`; `ValidationTaskExecutor.BuildSimulationCache` is now async and returns `(SimulationCache, IDisposable?)`. `RunBacktestCommandHandler` switched to `await using` and `DebugSession.EventSink` to `IAsyncDisposable?`. All test suites green (Domain 1024, Application 517, Infrastructure 249 + 28 S3-contract skips, HistoryLoader 572, WebApi 158, Private 71). PR 5 added `S3FileStorage` (single-PUT atomic publish; `MemoryStream`-backed `IObjectWriteSession`; `KeyPrefix`-scoped keys; paginated `ListObjectsV2`; batched `DeleteObjectsAsync`; `CopyObject` + `DeleteObject` `Move`), `S3TailIndex` (single Range-GET of the last 8 KiB — **chosen over the original sidecar design** because the writer base class only consults `IPartitionTailIndex` at resume, never per-flush, so a sidecar would have added a PutObject per flush for no payoff), and `StorageConfigMigration` (warns + auto-aliases legacy `HistoryLoader:DataRoot` / `CandleStorage:DataRoot` to `Storage:Local:DataRoot`). DI selection moved into `DependencyInjection.BuildFileStorage` / `BuildTailIndex` so the main host and `HistoryLoader.WebApi` make the same backend choice. MinIO-based contract suite `S3FileStorageContractTests` inherits `FileStorageContractTests` and is gated on `STORAGE_TEST_S3`.

## Context

Today the codebase writes and reads CSV/JSON files via direct `File.*` / `Directory.*` / `FileStream` calls scattered across HistoryLoader writers, backtest read-path loaders, and metadata managers. A thin `IFileStorage` exists in `src/AlgoTradeForge.Application/IO/` but only covers text-line read/write (`ReadLines`, `WriteAllText`) and is consumed by exactly one component (`JsonlFileSink`).

We want all file-system touchpoints in HistoryLoader and the backtest history repositories to flow through `IFileStorage` so the storage layer can be swapped at runtime between a local-filesystem backend and an S3 backend. This unlocks running the engine against a shared object-store dataset (e.g. backtests on EC2 reading from S3, multiple HistoryLoader instances publishing into S3) without per-call-site changes.

Decisions already taken (planning phase):
- **Scope:** read + write on both sides.
- **Append model:** buffer-then-PUT per partition (writers accumulate, flush whole partitions atomically). Accepts a small crash window of unflushed rows.
- **Surface:** reuse `IFileStorage` as the single abstraction; make as much code as possible storage-agnostic.

## Design

### Abstraction surface (replacement `IFileStorage`)

The interface is reshaped to be object-store-native **and fully async** — the workload is HTTP-bound on S3 and benefits from non-blocking I/O on local disk too. By convention there is no `Async` suffix on method names; the interface is async-only. Local FS emulates the object-store semantics; S3 implements them directly. Keys are **flat slash-delimited strings** — `"binance/BTCUSDT/candles/2026-05_1h.csv"` — and the local backend translates `/` → `Path.DirectorySeparatorChar` at its boundary. There is no `CreateDirectory` in the interface; the local impl creates parent dirs as needed when materializing a write.

```csharp
public interface IFileStorage
{
    // existence + discovery
    Task<bool> Exists(string key, CancellationToken ct = default);
    IAsyncEnumerable<string> ListKeys(string prefix, string? suffix = null, bool recursive = true, CancellationToken ct = default);

    // read (atomic snapshot; S3 GET, local FileShare.Read)
    Task<Stream> OpenRead(string key, CancellationToken ct = default);
    Task<string> ReadAllText(string key, CancellationToken ct = default);
    Task<string[]> ReadAllLines(string key, CancellationToken ct = default);
    IAsyncEnumerable<string> ReadLines(string key, CancellationToken ct = default);              // streamed
    Task<byte[]> ReadAllBytes(string key, CancellationToken ct = default);

    // write (atomic publish of a complete object)
    Task WriteAllText(string key, string content, Encoding? encoding = null, CancellationToken ct = default);
    Task WriteAllLines(string key, IEnumerable<string> lines, CancellationToken ct = default);
    Task WriteAllBytes(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default);

    // streaming write (explicit commit required; local = .tmp+Move, S3 = MemoryStream/temp buffer → PutObject)
    Task<IObjectWriteSession> OpenWriteSession(string key, CancellationToken ct = default);

    // mutation
    Task Delete(string key, CancellationToken ct = default);
    Task DeleteByPrefix(string prefix, CancellationToken ct = default);     // replaces Directory.Delete(dir, recursive: true)
    Task Move(string sourceKey, string destinationKey, bool overwrite, CancellationToken ct = default);
}

public interface IObjectWriteSession : IAsyncDisposable
{
    Stream Stream { get; }                              // write here
    Task Commit(CancellationToken ct = default);        // make visible atomically — caller MUST invoke explicitly
    Task Abort(CancellationToken ct = default);         // discard
    // DisposeAsync defaults to Abort when neither Commit nor Abort was called.
    // This guarantees cancellation/exception mid-write never publishes partial data.
}
```

Notes:
- **No `Append` verb in the interface.** All append-style writers shift to the buffer-then-PUT model below.
- **Atomic publish is the only write semantics.** `WriteAllText` and `OpenWriteSession`-then-`Commit` both commit atomically. Local impl uses `.tmp + File.Move(overwrite)`. S3 impl uses a single `PutObject`.
- **`ReadOnlyMemory<byte>` (not `Span<byte>`)** on `WriteAllBytes` — `Span` can't cross async boundaries because it's a ref struct.
- **`FileShare` is an implementation detail** that disappears from callers. Local backend keeps the existing Windows-safe pattern (`FileShare.Read` on writes, `FileShare.ReadWrite` on reads).
- **Move on S3** is `CopyObject` + `DeleteObject` — not atomic, but the only consumers (atomic rename of a temp file) are eliminated by `OpenWriteSession`. The remaining `Move` consumers (rare) accept best-effort semantics on S3 and we document it.
- **`IRunSink.WriteMeta` is async** — it calls through `IFileStorage`. The two backtest command handlers (`RunBacktestCommandHandler`, `StartDebugSessionCommandHandler`) `await` it. `IRunSink.Write(ReadOnlyMemory<byte>)` — the per-event hot path — stays sync because it writes directly to its own `FileStream`, not through `IFileStorage`, and the backtest engine's emit loop is synchronous.

### New types

| Type | Location | Purpose |
|---|---|---|
| `IFileStorage` (reshaped) | `src/AlgoTradeForge.Application/IO/IFileStorage.cs` | Single abstraction, object-store-shaped. |
| `IObjectWriteSession` | `src/AlgoTradeForge.Application/IO/IObjectWriteSession.cs` | Commit-on-dispose write handle. |
| `StorageKeys` (static) | `src/AlgoTradeForge.Application/IO/StorageKeys.cs` | Single source of truth for key schemas: `CandlePartition(exchange, asset, year, month, interval)`, `FeedPartition(...)`, `FeedsManifest(asset)`, `FeedStatus(asset, feed)`, `DailyTickPartition(...)`, `RunFolder(...)`, `RunEventsLog(...)`, `RunMeta(...)`. Centralizes the schema so callers stop hand-concatenating paths. |
| `StorageOptions` | `src/AlgoTradeForge.Application/IO/StorageOptions.cs` | Bound from `Storage:*` config — `Backend` (`LocalFileSystem` \| `S3`), `Local:DataRoot`, `S3:{Endpoint, Region, Bucket, KeyPrefix, AccessKeyId, SecretAccessKey}`. |
| `LocalFileStorage` (renamed from `FileStorage`) | `src/AlgoTradeForge.Infrastructure/IO/LocalFileStorage.cs` | Local backend. `key` → `Path.Combine(DataRoot, key.Replace('/', sep))`. Preserves atomic-rename + `flushToDisk:true` semantics. Treats absolute keys as pass-through so legacy callers (still passing absolute paths in PR 1) continue to work. |
| `S3FileStorage` | `src/AlgoTradeForge.Infrastructure/IO/S3FileStorage.cs` | S3 backend using `AWSSDK.S3` NuGet (only new package). Uses bucket + key prefix from `StorageOptions`. Arrives in PR 5. |
| `IPartitionTailIndex` + `LocalTailIndex` + `SidecarTailIndex` | `src/AlgoTradeForge.Application/IO/IPartitionTailIndex.cs`, `src/AlgoTradeForge.Infrastructure/IO/LocalTailIndex.cs`, `SidecarTailIndex.cs` (PR 5) | Cheap last-row lookup so writers don't pull the whole object on restart. Exposes `GetLastTimestamp(key)` for candle/feed (col-0 dedup) and `GetLastLine(key)` for tick/book (col-3 / col-4 dedup — parser lives in the subclass). Local impl reads the file's 8 KiB tail; S3 impl reads a sidecar `{key}.tail` (one PutObject per flush). |

DI registration in `src/AlgoTradeForge.Infrastructure/DependencyInjection.cs`:

```csharp
services.Configure<StorageOptions>(config.GetSection("Storage"));
services.AddSingleton<IFileStorage>(sp => {
    var opt = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
    return opt.Backend switch {
        StorageBackend.S3 => new S3FileStorage(opt.S3, sp.GetRequiredService<ILogger<S3FileStorage>>()),
        _                 => new LocalFileStorage(opt.Local, sp.GetRequiredService<ILogger<LocalFileStorage>>()),
    };
});
services.AddSingleton<IPartitionTailIndex>(...);   // mirror selection
```

### Buffer-then-PUT writer pattern

The four hot append writers — `CandleCsvWriter`, `FeedCsvWriter`, `DailyTickCsvWriter`, `DailyBookTickerCsvWriter` — share a common base class `BufferedPartitionWriter` in `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/Buffered/`:

1. **Resume** → on first contact with a partition (or after restart), the writer calls `IPartitionTailIndex.GetLastTimestamp(key)` (candle/feed) or `GetLastLine(key)` (tick/book) to recover only the dedup **watermark**. The in-memory buffer is NOT hydrated with existing rows — that would re-read every byte of a multi-megabyte daily tick partition just to know the last id, which is wasteful and quadratic in flush count.
2. **AppendRow(...)** is synchronous and adds the formatted row to the buffer behind the per-partition semaphore. Rows whose dedup key is &le; the current watermark are silently dropped.
3. **Flush()** is async and publishes via read-merge-write: `Exists(key)` → if yes, `ReadAllLines(key)` and concatenate buffered rows; if no, emit header + buffered rows. The whole partition is then `WriteAllLines(key, merged)` — atomic on local FS (`.tmp + Move`) and on S3 (single `PutObject`). Triggered by:
   - month/day rollover (a new partition key in `AppendRow` creates a new buffer; the prior one stays dirty until the timer or threshold fires),
   - configurable interval (`HistoryLoader:Storage:FlushIntervalSeconds`, default 60 s) driven by a single `BufferedWriterFlushService` hosted service,
   - row count threshold (`HistoryLoader:Storage:FlushEveryRows`, default 1000) — fire-and-forget inside `AppendRow`, exceptions logged. A per-buffer `FlushInFlight` flag prevents back-pressure from stacking N concurrent flushes when the producer outpaces the publisher,
   - graceful shutdown via `IHostApplicationLifetime.ApplicationStopping` (`BufferedWriterFlushService.StopAsync`, bounded by `HistoryLoader:Storage:ShutdownFlushTimeoutSeconds`, default 30 s),
   - explicit `FlushAllAsync(ct)` from tests or operators.

The existing per-partition concurrency primitives stay: `WriteLockManager.GetLock(partitionKey)` is the only thing protecting buffer mutation and per-key flush — same role, different consumer (it used to serialize the file append). Torn-row recovery in `DailyTickCsvWriter` / `DailyBookTickerCsvWriter` is **deleted** — atomic publish makes partial rows structurally impossible.

`HistoryLoader:Storage:InMemoryBufferLimitMB` (default 16) **only emits a warning** in PR 3 when the buffer's running byte total (sum of row lengths) exceeds the threshold; spill-to-disk is deferred until measurement shows it's needed. Realistic worst case at 60 s / 1000-row flush settings is ~80 KB per partition.

**Flush cost scales with current partition size, not row count.** Each flush reads the existing object whole, appends buffered rows, then rewrites the whole partition. For monthly-partitioned candle/feed files this is fine (kilobytes per flush). For daily tick / book-ticker files growing through the day, the daily total bytes rewritten is roughly `Σᵢ partition_size_i` — i.e. quadratic in partition size, linear in the number of intra-day flushes. The mitigation is partition granularity: ticks/book are already day-partitioned, and the file rolls at UTC midnight. If a single day exceeds the perf budget on S3, the next step is sub-day partitioning, not in-place append.

Crash semantics: rows still in the in-memory buffer at hard-kill are lost. Worst-case loss is `FlushIntervalSeconds` worth (or `FlushEveryRows`, whichever trips first) per active partition. This is the deliberate buffer-then-PUT tradeoff for object-store compatibility.

**Cross-project dependency note:** `AlgoTradeForge.HistoryLoader.Infrastructure` now references both `AlgoTradeForge.Application` (for `IFileStorage` / `IPartitionTailIndex`) and `AlgoTradeForge.Infrastructure` (for `LocalFileStorage` / `LocalTailIndex`). HistoryLoader does its own DI registration rather than calling `AddInfrastructure`, which would pull in SQLite repos and live-trading wiring the loader has no use for. PR 5 should consider extracting `LocalFileStorage` / `LocalTailIndex` (and the upcoming S3 backends) into a dedicated `AlgoTradeForge.Infrastructure.IO` package so HistoryLoader doesn't reach into the main Infrastructure project.

### Metadata + state managers

These already use `.tmp + File.Move(overwrite: true)` so they convert cleanly: each rewrite becomes one `WriteAllText(key, json)` call on `IFileStorage`.

- `FeedSchemaManager` (feeds.json): `ReaderWriterLockSlim` stays in front; load = `ReadAllText`, save = `WriteAllText`. The "re-read under write lock" pattern is preserved.
- `FeedStatusManager` (status.json): same shape. The explicit `fs.Flush(flushToDisk: true)` is folded into the local backend's `WriteAllText` implementation (it already does this in the existing local impl).
- `AppSettingsWriter` (appsettings.json): same shape. **However** — this writes to the binary's working directory, not to data storage. It stays on **local FS only** by injecting `LocalFileStorage` directly (or `IHostEnvironment.ContentRootFileProvider`), since "appsettings.json on S3" is nonsense.

### Aggregation pipeline (HistoryLoader)

The aggregation pipeline (`PartitionedSourceReader`, `PartitionedSinkWriter`, `OverwritePathWriter`, `AggregatedDirSweeper`) is the largest single chunk of file I/O. It maps cleanly:

- **Reader**: `Directory.EnumerateFiles(dir, pattern) → IFileStorage.ListKeys(prefix, suffix:".csv")`; `FileStream(Open, Read)` → `IFileStorage.OpenRead(key)`.
- **Sink writer**: the existing `.tmp → File.Move` cycle becomes a single `IObjectWriteSession` per partition. Partition overflow (`YYYY-MM.csv` → `.p01.csv` → `.p02.csv`) stays the same — just renames the **key**, not the file path.
- **Overwrite writer** (staging dir `.staging-jobId/` then atomic swap): becomes "write to a staging key prefix, then `Move` each completed key into place, then `DeleteByPrefix` the staging prefix". Not atomic across the whole job on S3 (only per-key), but every existing consumer already tolerates partial staging because the sweeper cleans up on next boot.
- **Sweeper**: `Directory.EnumerateFiles(*.tmp, AllDirectories)` → `ListKeys(prefix, suffix:".tmp", recursive:true)`; `Directory.Delete(recursive)` → `DeleteByPrefix`.

### Backtest read-path

These are all read-only and translate one-for-one:

- `PartitionedCsvBarLoader`: `Directory.EnumerateFiles + FileStream` → `ListKeys + OpenRead`. Partition collision detection unchanged (operates on the returned key list).
- `CsvFeedSeriesLoader`: same shape.
- `FeedContextBuilder` (feeds.json): `File.Exists + FileStream + JsonSerializer` → `Exists + OpenRead + JsonSerializer`.
- `FileSystemAssetRepository`: same shape (also reads feeds.json).
- `FileSystemAvailableAssetsProvider`: directory scan → `ListKeys(prefix, recursive:false)`, group by key prefix to materialize the exchange/asset list. Caching stays.

### Run/event/cache storage

- `JsonlFileSink` (`src/AlgoTradeForge.Infrastructure/Events/JsonlFileSink.cs`): already on `IFileStorage`. The current `FileMode.CreateNew + StreamWriter` for live event tailing needs an `OpenWriteSession` upgrade with periodic flush (events arrive throughout a run; one PutObject per event is prohibitive on S3). Adopts the same flush-interval policy as `BufferedPartitionWriter`.
- `SimulationCacheFileStore`: binary read/write, single-shot per cache entry — `WriteAllBytes` / `OpenRead` map cleanly.
- `SqliteRunRepository`, `SqliteValidationRepository`, `SqliteTradeDbWriter`, `SqliteEventIndexBuilder`: **stay on local disk.** SQLite over S3 is not practical (random-access mmap, page-level writes). Document this in `StorageOptions` and inject `LocalFileStorage` directly into the SQLite repositories. Add a startup-time validation that errors clearly if someone tries to point the SQLite root at an S3 key.

### Configuration

```json
{
  "Storage": {
    "Backend": "LocalFileSystem",
    "Local": {
      "DataRoot": ""    // empty → %LOCALAPPDATA%/AlgoTradeForge/History
    },
    "S3": {
      "Endpoint": "https://fsn1.your-objectstorage.com",  // Hetzner FSN1 by default; clear to "" for real AWS
      "Region": "fsn1",                                   // must match endpoint subdomain on Hetzner
      "Bucket": "algotradeforge-history",
      "KeyPrefix": "prod/",
      "AccessKeyId": "",        // empty → use default credential chain (env / IAM / SSO)
      "SecretAccessKey": ""
    }
  },
  "HistoryLoader": {
    "Storage": {
      "FlushIntervalSeconds": 60,
      "FlushEveryRows": 1000,
      "InMemoryBufferLimitMB": 16
    }
  }
}
```

`HistoryLoader.DataRoot` and `CandleStorage.DataRoot` keys become **deprecated aliases** that map to `Storage:Local:DataRoot` for one release; emit a warning at startup if either is set.

## Files to modify (critical paths)

**New files (Application layer):**
- `src/AlgoTradeForge.Application/IO/IFileStorage.cs` (reshape) — PR 1 ✅
- `src/AlgoTradeForge.Application/IO/IObjectWriteSession.cs` — PR 1 ✅
- `src/AlgoTradeForge.Application/IO/StorageKeys.cs` — PR 1 ✅
- `src/AlgoTradeForge.Application/IO/StorageOptions.cs` — PR 1 ✅
- `src/AlgoTradeForge.Application/IO/IPartitionTailIndex.cs` — PR 1 ✅

**New files (Infrastructure layer):**
- `src/AlgoTradeForge.Infrastructure/IO/LocalFileStorage.cs` (renamed from `FileStorage.cs`, reshape) — PR 1 ✅
- `src/AlgoTradeForge.Infrastructure/IO/S3FileStorage.cs` — PR 5
- `src/AlgoTradeForge.Infrastructure/IO/LocalTailIndex.cs` — PR 1 ✅
- `src/AlgoTradeForge.Infrastructure/IO/SidecarTailIndex.cs` — PR 5
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/Buffered/BufferedPartitionWriter.cs` — PR 3

**Files to update (route through `IFileStorage`):**

Backtest read-path (PR 2):
- `src/AlgoTradeForge.Infrastructure/History/PartitionedCsvBarLoader.cs`
- `src/AlgoTradeForge.Infrastructure/History/CsvFeedSeriesLoader.cs`
- `src/AlgoTradeForge.Infrastructure/History/FeedContextBuilder.cs`
- `src/AlgoTradeForge.Infrastructure/History/FileSystemAssetRepository.cs`
- `src/AlgoTradeForge.Infrastructure/History/FileSystemAvailableAssetsProvider.cs`

HistoryLoader writers — subclass `BufferedPartitionWriter` (PR 3):
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/CandleCsvWriter.cs`
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedCsvWriter.cs`
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/DailyTickCsvWriter.cs`
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/DailyBookTickerCsvWriter.cs`

HistoryLoader state (PR 4a):
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/State/FeedStatusManager.cs`
- `src/AlgoTradeForge.HistoryLoader.WebApi/AppSettingsWriter.cs` (binds to `LocalFileStorage` only)

HistoryLoader schema manager (PR 4a.1):
- `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedSchemaManager.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Catalog/FeedCatalog.cs` (5 sync→async API methods)
- `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/AggregationEndpoints.cs`
- `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/FundingInfoRefreshService.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/AggregationPipeline.cs` (its three `Ensure*` call sites)

HistoryLoader aggregation (PR 4b):
- `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/PartitionedSourceReader.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/PartitionedSinkWriter.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/OverwritePathWriter.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/AggregatedDirSweeper.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/StartupSweepService.cs` (calls sweeper)
- `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/AggregationPipeline.cs`

Events + cache (PR 4c):
- `src/AlgoTradeForge.Infrastructure/Events/JsonlFileSink.cs` (hot event path → `OpenWriteSession` + periodic flush; `WriteMeta` is already on `IFileStorage`)
- `src/AlgoTradeForge.Infrastructure/Validation/SimulationCacheFileStore.cs`

DI:
- `src/AlgoTradeForge.Infrastructure/DependencyInjection.cs` (backend selection) — PR 1 partial, PR 5 completes
- `src/AlgoTradeForge.WebApi/Program.cs` (config binding for `Storage:*`) — PR 5
- `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (same) — PR 5

NuGet (PR 5):
- `src/AlgoTradeForge.Infrastructure/AlgoTradeForge.Infrastructure.csproj` — add `AWSSDK.S3` (latest stable).

## Test plan

Phasing the test work mirrors the rollout:

1. **Unit tests for `IFileStorage` contract** (PR 1 ✅): `tests/AlgoTradeForge.Infrastructure.Tests/IO/FileStorageContractTests.cs` is an abstract suite of 14 contract assertions. `LocalFileStorageContractTests` derives from it and runs the suite against `LocalFileStorage` over a temp dir. When PR 5 lands, an `S3FileStorageContractTests` class will inherit the same suite to run against MinIO (skipped unless `STORAGE_TEST_S3` env var is set).
   - Tests: round-trip text, round-trip bytes, list with prefix + suffix, list non-recursive, atomic publish via session, abort leaves no visible object, overwrite, Move overwrite semantics, Delete idempotency, DeleteByPrefix clears nested keys.

2. **Existing `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/` test classes** (PR 3) are converted to inject `LocalFileStorage` over a temp dir instead of calling `File.*` directly. They run end-to-end through the same buffered-writer code path that S3 will use, catching the bulk of regressions on local before any S3 wiring.

3. **Resume tests** (PR 3):
   - Write 100 rows → kill before flush → restart writer → verify in-memory buffer is hydrated from existing partition (use a synthetic partition with 100 known rows on disk to seed).
   - Verify `IPartitionTailIndex` short-circuit: writer that only needs `lastTimestamp` on restart reads the sidecar, not the whole partition (assert via spy on `OpenRead` calls).

4. **Backtest read-path integration** (PR 2 + PR 5):
   - Existing backtest tests that use `HistoryTest` data root continue to pass against `LocalFileStorage`.
   - One new integration test seeds a MinIO bucket with the bundled benchmark BTCUSDT_1h CSVs, runs `BacktestPreparer` against `Storage:Backend=S3`, asserts the same trades produced as the local run. (Gated on `STORAGE_TEST_S3`.)

5. **End-to-end manual verification** (each PR):
   - `dotnet build AlgoTradeForge.slnx`
   - `dotnet test tests/AlgoTradeForge.Domain.Tests/` (sequential — never parallel; CLAUDE.md rule)
   - `dotnet test tests/AlgoTradeForge.Application.Tests/`
   - `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/`
   - `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
   - Start WebApi with `Storage:Backend=LocalFileSystem` → run a backtest via `/backtest` → confirm trade output unchanged versus pre-change baseline.
   - Repeat with `Storage:Backend=S3` against MinIO (local Docker) once the test fixture is in place.

6. **Performance baseline** (PR 3 — catch write-amplification regressions from buffer-then-PUT on the local backend):
   - `scripts/perf/save-baseline.ps1` on parent commit.
   - `scripts/perf/save-baseline.ps1` after the change.
   - `scripts/perf/compare-baseline.ps1 -Baseline previous -Candidate latest`.
   - Watch `Allocated` carefully on `BacktestBenchmarks.Backtest_5y_Hourly` and `OptimizationBenchmarks.Optimization_1000Trials_Parallel` (read-path) and add a synthetic ingestion benchmark if write-path regressions are suspected.

## Rollout / sequencing

The work decomposes into independently shippable PRs to keep review tractable:

1. **PR 1 — Abstraction + Local backend, no behavior change.** ✅ **Complete.** Reshaped `IFileStorage`, added `StorageKeys`, `StorageOptions`, `LocalFileStorage`, `IObjectWriteSession`, `IPartitionTailIndex` + `LocalTailIndex`. Migrated the existing `JsonlFileSink` consumer to keep compiling against the new surface. Added contract tests + tail-index tests. No call sites moved yet. Behavior identical. All test suites green (Domain 1024/1024, Application 517/517, Infrastructure 248/248, HistoryLoader 573/573, WebApi 158/158).

2. **PR 2 — Backtest read-path migration.** Route all five read-path classes through `IFileStorage`. Re-run benchmarks + integration tests. Use the absolute-path bypass in `LocalFileStorage.Resolve()` until call sites move to `StorageKeys`.

3. **PR 3 — `BufferedPartitionWriter` + writer migration.** ✅ **Complete.** Added `BufferedPartitionWriter` (read-merge-write flush, watermark-only resume, no buffer hydration), `BufferedWriterFlushService` (single hosted service driving periodic + shutdown flush), `HistoryLoaderStorageOptions`. Extended `IPartitionTailIndex` with `GetLastLine`. Migrated the four writers; deleted ~300 LOC of torn-row recovery + LRU dedup caches + active-day stream cache. Four `ResumeFrom` interfaces became `Task<...>` async with `CancellationToken`. Tests rewritten to inject `LocalFileStorage(tempDir)` + `LocalTailIndex`; added new resume-watermark / tail-spy / threshold-flush / failure-retry tests.

4. **PR 4 — Metadata + aggregation migration.** Split into three independently shippable chunks because the call-site fan-out is too broad for one PR and the aggregation pipeline's streaming append semantics need their own design pass:

   - **PR 4a (this stage) — `IFeedStatusStore` + `ISettingsWriter` async; `LocalFileStorage` flush hardening.** Convert `IFeedStatusStore` and `ISettingsWriter` to async (no `Async` suffix per Constitution v1.8.3) and route their implementations through `IFileStorage`. `AppSettingsWriter` binds `LocalFileStorage` directly because the binary's `appsettings.json` is not data-root content. Tighten `LocalWriteSession.Commit` to call `Flush(flushToDisk: true)` — the doc has always promised this and `FeedStatusManager` relied on it for NTFS zero-extension safety. Update ~14 caller sites (`FeedCollectorBase`, three `*StreamService` flushers, `AggTradeFeedCollector`, `CandleFeedCollector`, `GenericFeedCollectorBase`, `SymbolCollector`, `StatusEndpoints` GET handlers) to await. `ISchemaManager` (and the `FeedSchemaManager` `ReaderWriterLockSlim`-vs-`SemaphoreSlim` rework) intentionally stays out of 4a — its `Load` flows through `FeedCatalog`'s `IMemoryCache`-backed sync API and a half-dozen sync endpoint handlers, all of which would need to go async in lock-step. That sub-migration is PR 4a.1.

   - **PR 4a.1 — `ISchemaManager` + `FeedCatalog` + Aggregation endpoints async.** Convert every `ISchemaManager` method to async, swap `ReaderWriterLockSlim` for per-asset `SemaphoreSlim` in `FeedSchemaManager`, propagate `Task` through `IFeedCatalog` (5 methods) and the catalog/aggregation/status endpoint handlers and `FundingInfoRefreshService`/`AggregationPipeline`. Update the test fan-out (`FeedSchemaManagerTests`, `FeedSchemaManagerCascadeTests`, `FeedSchemaManagerStressTests`, `FeedCatalogTests`, `FeedMetadataValidationTests`, `StartupSweepTests`, all `AggregationPipeline*Tests`).

   - **PR 4b — Aggregation pipeline.** `PartitionedSourceReader` → async + `IFileStorage.ListKeys` / `OpenRead`. `PartitionedSinkWriter` + `OverwritePathWriter` → `IObjectWriteSession`-based streaming with the `.tmp + Move` cycle becoming explicit `Commit()`. `AggregatedDirSweeper` + `StartupSweepService` → `ListKeys` / `DeleteByPrefix`. `AggregationPipeline.Run` becomes async (it orchestrates all of the above). The sweeper's "enumerate immediate subdirs" semantics on S3 need to be derived from key prefixes since S3 has no real directories — this is the structural reason for the separate PR.

   - **PR 4c — Run/event/cache storage.** `JsonlFileSink` hot event path → `OpenWriteSession` with periodic flush (per-event PutObject is prohibitive on S3; the session-based pattern with a flush hosted service mirrors `BufferedPartitionWriter`). `SimulationCacheFileStore` → `OpenWriteSession` for binary streaming + `OpenRead` for typed binary reads. The hot tailing-while-writing pattern of `events.jsonl` is structurally different from per-partition append (live tailers want incremental visibility), so the session policy needs design that's distinct from `BufferedPartitionWriter`'s read-merge-write.

   Migration of call sites from absolute paths to `StorageKeys` (so the absolute-path bypass in `LocalFileStorage` can be removed) is deferred to PR 4b/4c. PR 4a keeps using absolute paths via the bypass — every modified call site builds the final path via `Path.Combine` and hands it to `IFileStorage`, which forwards rooted paths unchanged.

5. **PR 5 — S3 backend.** Add `AWSSDK.S3` package, implement `S3FileStorage` + `SidecarTailIndex`, wire DI selection across both hosts (WebApi, HistoryLoader.WebApi), MinIO-based contract tests (inheriting `FileStorageContractTests`), MinIO-based integration test for backtest reads. Deprecate `HistoryLoader.DataRoot` / `CandleStorage.DataRoot` config keys with one-release migration warning.

## Post-PR-5 follow-ups

- **Multipart upload threshold for `S3WriteSession`.** Current `IObjectWriteSession` buffers the entire payload in memory and issues one `PutObject` on `Commit`. That gives free atomicity but pins the full file in RAM until commit. For event sinks and partition writers on long runs, this can mean tens or hundreds of MB resident per open session. Switch to S3 multipart upload (`InitiateMultipartUpload` / `UploadPart` per ~5 MB chunk / `CompleteMultipartUpload`) above a configurable threshold (e.g. 8 MB). `CompleteMultipartUpload` preserves the "atomic publish on commit" contract; `AbortMultipartUpload` covers the failure path. Local backend is unaffected.
- **Server-side conditional `Move`.** `S3FileStorage.Move(overwrite: false)` is check-then-act today; the only current caller (`PartitionedSinkWriter` cross-month sticky path) serializes via `WriteLockManager`, so the race is dormant. Promote to `CopyObjectRequest.IfNoneMatchETag = "*"` once AWS SDK + MinIO support is verified end-to-end.
