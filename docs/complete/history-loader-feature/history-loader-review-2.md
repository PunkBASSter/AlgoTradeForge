# History Loader — Review 2 Fix Plan

Fixes for issues identified in the second code review of branch `019-history-loader`.
Items from the first review (`history-loader-refactoring-review-plan.md`) have been
addressed in commits `089f11f`–`7e1cdaa`. This plan covers **new findings only**.

---

## Phase 1 — Critical (functional bugs that break read/write integration)

### C1. DataRoot default mismatch between HistoryLoader and WebApi

**Problem:** `HistoryLoaderOptions.DefaultDataRoot` resolves to `…/AlgoTradeForge/History`
while `CandleStorageOptions.DefaultDataRoot` resolves to `…/AlgoTradeForge/Candles`. With
default config, the HistoryLoader writes data the WebApi will never find.

**Files:**
- `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs`
- `src/AlgoTradeForge.Application/CandleIngestion/CandleStorageOptions.cs`

**Fix:** Align both defaults to `History` (the new canonical location). The HistoryLoader
writes klines AND auxiliary feeds, so `Candles` is too narrow a name. Update
`CandleStorageOptions.DefaultDataRoot`:

```csharp
// Before
public static readonly string DefaultDataRoot =
    Path.Combine(Environment.GetFolderPath(...), "AlgoTradeForge", "Candles");

// After
public static readonly string DefaultDataRoot =
    Path.Combine(Environment.GetFolderPath(...), "AlgoTradeForge", "History");
```

Also verify `appsettings.json` in both projects doesn't override with conflicting paths.

### C2. `CsvDataSource` not updated to use `AssetDirectoryName.From()`

**Problem:** `HistoryRepository.cs` was correctly updated to use `AssetDirectoryName.From()`
but `CsvDataSource.cs` still uses `asset.Name`. For a `CryptoPerpetualAsset` named "BTCUSDT",
the HistoryLoader writes to `BTCUSDT_fut/` but `CsvDataSource` looks in `BTCUSDT/`.

**File:** `src/AlgoTradeForge.Infrastructure/History/CsvDataSource.cs` (~line 30)

**Fix:**
```csharp
// Before
var dir = Path.Combine(_dataRoot, asset.Exchange, asset.Name);

// After
var dir = Path.Combine(_dataRoot, asset.Exchange, AssetDirectoryName.From(asset));
```

### C3. `GapDetectionTests` re-implements production logic — coverage illusion

**Problem:** The test file contains a local `DetectGaps` helper that copies the gap detection
algorithm rather than testing `FeedCollectorBase.DetectGap`. If the production logic changes,
these tests won't catch the regression.

**File:** `src/AlgoTradeForge.HistoryLoader.Tests/Collection/GapDetectionTests.cs`

**Fix:** Make `FeedCollectorBase.DetectGap` internal (it's already in a project with
`InternalsVisibleTo` for the test project). Update the tests to call the real method:

```csharp
// Before — tests a local reimplementation
private static List<DataGap> DetectGaps(long[] timestamps, long expectedMs) { ... }

// After — tests the real code
// Remove the local DetectGaps method entirely. Call FeedCollectorBase.DetectGap directly
// in each test, iterating timestamps and collecting gaps.
```

---

## Phase 2 — Medium (correctness issues, not currently breaking but risk under load)

### M1. `FeedCsvWriter.ResumeFrom` does not seed `_lastWrittenTimestamps`

**Problem:** `CandleCsvWriter.ResumeFrom` correctly calls `_lastWrittenTimestamps.AddOrUpdate`
after finding the last timestamp, ensuring the in-memory dedup guard works after restart.
`FeedCsvWriter.ResumeFrom` does NOT seed the dictionary — the dedup guard is inert until the
first new write. The application-layer `GenericFeedCollectorBase` has a secondary filter that
mitigates this, but the writer is internally inconsistent.

**File:** `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/FeedCsvWriter.cs`

**Fix:** Add the same seeding pattern as `CandleCsvWriter`:

```csharp
// In FeedCsvWriter.ResumeFrom, after finding lastTs:
var dedupKey = $"{assetDir}/{feedName}/{interval}";
_lastWrittenTimestamps.AddOrUpdate(dedupKey, lastTs, (_, _) => lastTs);
return lastTs;
```

### M2. `PartitionedCsvBarLoader.Load()` uses `long.Parse()` — crashes on malformed data

**Problem:** Unlike `CsvFeedSeriesLoader` which uses `TryParse` and skips malformed rows,
`PartitionedCsvBarLoader` uses raw `long.Parse()`. A single corrupt CSV row aborts the
entire series load.

**File:** `src/AlgoTradeForge.Infrastructure/History/PartitionedCsvBarLoader.cs` (~lines 57-68)

**Fix:** Replace `long.Parse` with `long.TryParse`, skip malformed rows:

```csharp
// Before
var bar = new Int64Bar
{
    Timestamp = long.Parse(parts[0]),
    Open      = long.Parse(parts[1]),
    ...
};

// After
if (!long.TryParse(parts[0], out var timestamp) ||
    !long.TryParse(parts[1], out var open) ||
    !long.TryParse(parts[2], out var high) ||
    !long.TryParse(parts[3], out var low) ||
    !long.TryParse(parts[4], out var close) ||
    !long.TryParse(parts[5], out var volume))
    continue;

var bar = new Int64Bar
{
    Timestamp = timestamp, Open = open, High = high,
    Low = low, Close = close, Volume = volume
};
```

### M3. `BackfillOrchestrator.RunAsync` disposes `SemaphoreSlim` while tasks may hold it

**Problem:** `using var semaphore = new SemaphoreSlim(...)` disposes at scope exit, but
cancelled tasks may still be awaiting the semaphore. The `catch (ObjectDisposedException)`
at line 97 confirms this is a known race being papered over.

**File:** `src/AlgoTradeForge.HistoryLoader.Application/Collection/BackfillOrchestrator.cs`

**Fix:** Remove `using` — `SemaphoreSlim` without `AvailableWaitHandle` holds no OS resources,
so GC cleanup is safe and avoids the race:

```csharp
// Before
using var semaphore = new SemaphoreSlim(maxConcurrency);

// After
var semaphore = new SemaphoreSlim(maxConcurrency);
```

Also remove the `catch (ObjectDisposedException)` block in `BackfillSymbolAsync` since it
becomes unreachable.

### M4. `CollectionCircuitBreaker` has no reset mechanism

**Problem:** Once tripped by a transient HTTP 418, all collection stops permanently until
process restart. No API endpoint or timer can reset it.

**Files:**
- `src/AlgoTradeForge.HistoryLoader.Application/Collection/ICollectionCircuitBreaker.cs`
- `src/AlgoTradeForge.HistoryLoader.Application/Collection/CollectionCircuitBreaker.cs`
- `src/AlgoTradeForge.HistoryLoader/Endpoints/StatusEndpoints.cs` (new endpoint)

**Fix:** Add `Reset()` to the interface and implementation:

```csharp
// ICollectionCircuitBreaker.cs
public interface ICollectionCircuitBreaker
{
    bool IsTripped { get; }
    void Trip();
    void Reset();
}

// CollectionCircuitBreaker.cs
public void Reset() => _tripped = false;
```

Wire a reset endpoint:
```csharp
// In StatusEndpoints.cs
group.MapPost("/circuit-breaker/reset", (ICollectionCircuitBreaker cb) =>
{
    cb.Reset();
    return Results.Ok();
});
```

### M5. `IntervalParser` vs `PartitionedCsvBarLoader.IntervalToString` — divergent interval sets

**Problem:** `IntervalParser` supports 12 intervals. `PartitionedCsvBarLoader.IntervalToString`
supports 7, with a silent fallback `$"{(int)interval.TotalMinutes}m"` that produces
un-validated strings. The two may produce inconsistent filenames for the same data.

**Files:**
- `src/AlgoTradeForge.HistoryLoader.Domain/IntervalParser.cs`
- `src/AlgoTradeForge.Infrastructure/History/PartitionedCsvBarLoader.cs` (~lines 126-137)

**Fix:** Make `PartitionedCsvBarLoader.IntervalToString` delegate to `IntervalParser`:

Since `IntervalParser` is in `AlgoTradeForge.HistoryLoader.Domain` (which `Infrastructure`
doesn't reference), either:
- **Option A**: Move `IntervalParser` to `AlgoTradeForge.Domain` (shared), or
- **Option B**: Duplicate the complete 12-interval mapping into `PartitionedCsvBarLoader` and
  throw on unknown intervals instead of using the silent fallback.

Option B is simpler and avoids a cross-project dependency:
```csharp
// Replace the silent fallback
_ => throw new ArgumentException($"Unsupported interval: {interval}")
```

---

## Phase 3 — Low (design improvements, not bugs)

### L1. `FeedStatus.Gaps` uses mutable `List<DataGap>`

**Problem:** `FeedStatus` uses `init`-only properties suggesting immutability, but `Gaps` is
`List<DataGap>` which is freely mutable after construction.

**File:** `src/AlgoTradeForge.HistoryLoader.Domain/FeedStatus.cs`

**Fix:** Change to `IReadOnlyList<DataGap>`:
```csharp
public IReadOnlyList<DataGap> Gaps { get; init; } = [];
```

Update `FeedCollectorBase.UpdateFeedStatus` to construct the list explicitly before passing it.

### L2. No input validation on `BackfillRequest`

**Problem:** Empty/whitespace `Symbol` passes through. Invalid feed names are silently ignored.

**File:** `src/AlgoTradeForge.HistoryLoader/Endpoints/BackfillEndpoints.cs`

**Fix:** Add validation in the endpoint handler:
```csharp
if (string.IsNullOrWhiteSpace(request.Symbol))
    return Results.BadRequest("Symbol is required");
```

### L3. `CandleFeedCollector` accepts nullable `ISpotDataFetcher?` but throws at runtime

**Problem:** If a spot asset is configured and `spotClient` is null, it throws
`InvalidOperationException` at collection time rather than failing fast at startup.

**File:** `src/AlgoTradeForge.HistoryLoader.Application/Collection/Feeds/CandleFeedCollector.cs`

**Fix:** Make `ISpotDataFetcher` non-nullable and ensure it is registered in DI. If spot
support is truly optional, use a null-object pattern that returns an empty async enumerable.

### L4. `StatusEndpoints.GetSymbolStatus` returns domain `FeedStatus` directly

**Problem:** API response is coupled to the domain model. If `FeedStatus` changes, the API
contract changes silently.

**File:** `src/AlgoTradeForge.HistoryLoader/Endpoints/StatusEndpoints.cs`

**Fix:** Map to a DTO (like `GetAllStatus` already does with `FeedStatusSummary`):
```csharp
public sealed record FeedStatusDetail(
    string FeedName, string Interval, long FirstTimestamp,
    long LastTimestamp, int RecordCount, int GapCount);
```

### L5. `SourceRateLimiter.BaseUrl` is dead code

**File:** `src/AlgoTradeForge.HistoryLoader.Infrastructure/RateLimiting/SourceRateLimiter.cs`

**Fix:** Remove the `BaseUrl` property. If needed later for diagnostics, re-add it.

---

## Phase 4 — Duplication reduction

### D1. Extract shared `BinanceKlineParser`

**Problem:** `ParseKlineBatch` is identical between `BinanceFuturesClient.cs` and
`BinanceSpotClient.cs`.

**Fix:** Create `Binance/BinanceKlineParser.cs` with a static `ParseBatch` method. Both
clients delegate to it.

### D2. Merge `ParseRatioBatch` and `ParsePositionRatioBatch`

**Problem:** These two methods in separate partial class files are functionally identical.

**Fix:** Create a shared static method (e.g., in `BinanceKlineParser` or a new
`BinanceRatioParser`) and call from both partial files.

### D3. Consolidate magic feed name strings

**Problem:** Feed names like `"candles"`, `"funding-rate"`, `"mark-price"`, etc. appear as
string literals across 15+ files.

**Fix:** Create a static constants class:
```csharp
// src/AlgoTradeForge.HistoryLoader.Domain/FeedNames.cs
public static class FeedNames
{
    public const string Candles = "candles";
    public const string CandleExt = "candle-ext";
    public const string FundingRate = "funding-rate";
    public const string MarkPrice = "mark-price";
    public const string OpenInterest = "open-interest";
    public const string TakerVolume = "taker-volume";
    public const string LsRatioGlobal = "ls-ratio-global";
    public const string LsRatioTopAccounts = "ls-ratio-top-accounts";
    public const string LsRatioTopPositions = "ls-ratio-top-positions";
    public const string Liquidations = "liquidations";
}
```

### D4. Refactor `MarkPriceFeedCollector` to extend `GenericFeedCollectorBase`

**Problem:** `MarkPriceFeedCollector` manually reimplements resume, gap detection, and status
update logic that `GenericFeedCollectorBase` already provides.

**Fix:** Make it extend `GenericFeedCollectorBase`. Override `FetchAsync` to wrap the
kline-to-feed-record conversion. Remove the manual implementation of the collect loop.

### D5. Extract shared test helper for `FakeHandler`

**Problem:** `FakeHandler` + `BuildClient` + `JsonResponse` are copy-pasted across 9 Binance
test files.

**Fix:** Create `TestHelpers/FakeHttpHandler.cs` in the test project with a shared
`FakeHandler` class and static helper methods. Update all 9 test files to use it.

### D6. Extract generic `ScheduledCollectorService` base class

**Problem:** All 6 collector `BackgroundService` classes share identical boilerplate:
`PeriodicTimer` loop, circuit-breaker check, try/catch with 418 detection, asset filtering.

**Fix:** Create a base class parameterized by timer interval, feed filter, and asset type
filter:
```csharp
internal abstract class ScheduledCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }
    protected abstract string[] FeedNames { get; }
    protected abstract bool ShouldCollect(AssetCollectionConfig asset);

    protected sealed override async Task ExecuteAsync(CancellationToken ct) { ... }
}
```

Each concrete service becomes ~10 lines of configuration overrides.

---

## Phase 5 — Test coverage (highest-value gaps)

### T1. `SymbolCollector` unit tests

Test routing by feed name, spot-vs-futures filtering, HTTP 400 catch-and-log, unknown feed
name warning. Mock `IFeedCollector` instances with NSubstitute.

### T2. `BackfillOrchestrator` unit tests

Test `IsRunning`/mutex behavior, concurrent `TryRunSingleAsync` returning `false` for same
symbol, feed filtering by `Enabled`, `fromDate` override logic.

### T3. `AssetPathConvention` tests

Pure function, 4 valid cases + 1 error case. Straightforward ~20-line test class.

### T4. `BinanceRetryHelper` isolation tests

Test HTTP 5xx retry path, max-retry exhaustion for both 429 and 5xx, verify weight is passed
to the rate limiter on each attempt.

### T5. `CsvFeedSeriesLoader` malformed-data tests

Test: empty lines, fewer columns than header, non-numeric values, invalid timestamps.
Verify graceful skipping in all cases.

### T6. `FeedContextBuilder` error-path tests

Test: malformed `feeds.json` (invalid JSON → returns `null`), `feeds.json` with zero feeds
(→ returns `null`).

### T7. `PartitionedCsvBarLoader` malformed-row tests

Test: `parts.Length < 6` skip, empty line skip, non-numeric values skip (after M2 fix).

---

## Phase 6 — Convention fixes

### S1. Move HistoryLoader tests from `src/` to `tests/`

Move `src/AlgoTradeForge.HistoryLoader.Tests/` → `tests/AlgoTradeForge.HistoryLoader.Tests/`
to match the project convention. Update `AlgoTradeForge.slnx` paths.

### S2. Register `CsvFeedSeriesLoader` behind an interface

Create `ICsvFeedSeriesLoader` or generalize to `IFeedSeriesLoader`. Register in DI behind the
interface. Update `FeedContextBuilder` to accept the interface, enabling test mocking.

---

## Execution Order

| Phase | Items | Severity | Scope |
|-------|-------|----------|-------|
| 1     | C1, C2, C3 | Critical | 4 files, functional bugs |
| 2     | M1–M5 | Medium | 7 files, correctness |
| 3     | L1–L5 | Low | 5 files, design |
| 4     | D1–D6 | Low | 15+ files, duplication |
| 5     | T1–T7 | — | New test files |
| 6     | S1, S2 | — | Project structure |

After each phase: `dotnet build AlgoTradeForge.slnx && dotnet test src/AlgoTradeForge.HistoryLoader.Tests/`
After Phase 6: update test path to `tests/AlgoTradeForge.HistoryLoader.Tests/`
