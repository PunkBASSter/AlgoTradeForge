# Binance Archive Backfill — Phase 3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the heavy/derived archive sources — `ticks` (aggTrades), `funding-rate` (fundingRate ⋈ markPriceKlines), and `taker-volume` (derived from klines) — behind the existing materializer registry; guard oversized tick loads; and retire the live `taker-volume` REST collector. This closes the archive-backfill feature: after Phase 3 every replenishable Binance feed is materializable.

**Spec:** `docs/superpowers/specs/2026-07-07-binance-archive-backfill-design.md` §7.3 + §2 (materializer table) + §1 (classification). Read it first.

**Base:** `main` @ `b0b3586` (Phase 2 merged). Branch: `feat/archive-backfill-phase3`.

**Architecture:** Phase 3 is almost entirely **additive at one composition site** — three new `IArchiveMaterializer` implementations registered in `Infrastructure/DependencyInjection.cs`. Registration alone flips each feed's classification to *replenishable* (and therefore *lazy* under the Phase 2 `CollectionPolicy`); the eager/lazy policy, archive-first branch, coverage predicate, load jobs, and Data-tab UI all consume the new materializers unchanged. The two genuinely new mechanisms are (a) the **tick coverage predicate** — a `CompleteMonths` marker in `FeedStatus`, distinct from interval feeds' row-count check — and (b) the **tick disk-budget guard** — a new 422 in `LoadRequestValidator`. One cross-cutting correctness fix rides along: **tick price/qty must be stored as scaled `long`** (Int64 Money Convention) in both the archive and the live write paths, which currently disagree.

**Tech Stack:** C# 14 / .NET 10, xunit.v3 + NSubstitute (`tests/AlgoTradeForge.HistoryLoader.Tests/`); frontend Next.js 16 / Vitest 4 (`frontend/`); `System.IO.Compression` (in-box); no new NuGet packages.

**Scope decisions (owner-approved at Q&A, 2026-07-07):**
- **All three workstreams in this phase** (funding included, low-priority but small once the archive infra exists).
- **Spot `1s` klines DROPPED** — sub-minute is not needed yet and the wider platform (resolver source is a single global `SourceInterval`; downstream may not absorb ~60× row density) is not ready. Materializing it is cheap, but making spot backtests *consume* 1s spot-only requires a per-asset-type resolver change in the main Infrastructure — out of scope. Not tracked as a follow-up unless a concrete need arises.
- **Tick on-disk encoding = scaled `long` everywhere** (see Global Constraints "Tick encoding").
- **Disk-budget cap** = `LoadOptions.MaxTickMonthsPerRequest`, default **24**, configurable, validated `> 0`.

---

## Global Constraints

- **Commit mode (owner-approved, per handover):** each implementer commits ONLY the files it created/modified for its task. `git add <explicit paths>` — NEVER `-A`, never `docs/superpowers/**`, never `.superpowers/**`, never README.md. This overrides the general no-auto-staging rule for this branch. Commit messages end with trailers:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` and
  `Claude-Session: https://claude.ai/code/session_01LWgP1QLktmUYgoiJBqz2bV`.
  The spec/plan/vision-doc edits (Tasks 13 + ledger) are committed by the CONTROLLER, not implementers.
- **Only ONE `dotnet` process at a time.** Never run build/test in parallel. Frontend `npm test` runs from `frontend/`.
- Backend test command: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~<TestClass>"`.
- No `Async` suffix on new async methods; `CancellationToken ct = default` on every async signature.
- One type per file, named after the type (single-line records accompanying an interface may share its file).
- Comments: only non-obvious facts, terse. **Warnings are errors** — build must be 0/0.
- **Tests:** pass `TestContext.Current.CancellationToken` to every awaited call in test bodies (xUnit1051 — snippets omit it for brevity; add when transcribing).
- **`SemaphoreSlim`:** `using var _ = await gate.LockAsync(ct);` via `SemaphoreSlimExtensions` — never `WaitAsync` + try/finally.
- **Int64 Money Convention:** monetary/price values are scaled `long` via `MoneyConvert.ToLong(value * multiplier)` where `multiplier = (decimal)Math.Pow(10, DecimalDigits)` — NEVER raw `(long)` casts. Timestamps / counters / agg_ids are plain `long` — raw casts fine.
- **`DataGap` semantics (both ends PRESENT rows):** `FromMs` = last row before the hole, `ToMs` = first row after it; missing rows strictly inside = `(ToMs − FromMs)/interval − 1`. Repo-wide.
- **Archive gap rule (Phase 2 live-smoke fix — DO NOT regress):** the ARCHIVE path records a `DataGap` for ANY missing slot (`jump > interval`); the streaming/REST path keeps its `GapThresholdMultiplier` (>2×). Archive months have exact fixed slots.
- **Closed-months ownership:** the archive touches only fully closed months; the current UTC month belongs to the REST/stream tail. `CoverFromArchive` returns `min(toMs, currentMonthStart)`.
- **Tick encoding (Phase 3 decision):** tick `price` and `qty` are stored as scaled `long` = `MoneyConvert.ToLong(value * 10^DecimalDigits)` — the SAME `10^DecimalDigits` the candle materializer applies to OHLC+volume. Rationale: the production aggregation pipeline's only scale is `AssetScaleContextFactory.FromDecimalDigits` (`QuantityScale = 1`); the two-arg `ScaleContext(tickSize, quantityStepSize)` is test-only. There is no separate qty step anywhere in HistoryLoader, so `10^DecimalDigits` is the only runtime multiplier and it is what `PartitionedSourceReader` parses back (`long.TryParse`). Both the new materializer AND the live `DailyTickCsvWriter`/stream path must use it (Task 3) — otherwise a tick feed's archived closed months (scaled) and its current-month REST tail (raw decimal, today's bug) disagree and the aggregation reader throws.
- **Tick storage layout:** DAILY partitions `{assetDir}/ticks/{yyyy-MM-dd}.csv`, header `ts,price,qty,is_buyer_maker,agg_id` (5 cols, `is_buyer_maker` = `0`/`1`). The `CompleteMonths` marker is a coverage bookkeeping entry in `FeedStatus` ONLY — it does not change the on-disk daily layout. The tick read path (`PartitionedSourceReader.ReadTicks`) globs 14-char `yyyy-MM-dd.csv` files ONLY; a monthly tick file would be silently ignored.

## Collection-policy impact (plan-review decision — recommended default baked in)

`ticks`, `funding-rate`, and `taker-volume` are **currently enabled and eagerly collected** in `appsettings.json` (ticks via `TicksCollectorService` + `SpotAggTradeStreamService`; funding via `FundingRateCollectorService` cron; taker-volume via `RatioCollectorService`). Registering their materializers (Task 8) flips each to *replenishable* → the Phase 2 `CollectionPolicy` default makes them **lazy**, so their eager collection stops (the archive recovers them on demand). This mirrors the candles decision in Phase 2 — except candles were kept `Eager:true` for the stale-tail; these three are **left lazy by default** because:
- **taker-volume:** its live collector is deleted outright (Task 7) — materializer-only henceforth.
- **funding-rate:** archive + REST both cover full depth; 8h cadence, no stale-tail urgency; lazy is fine.
- **ticks:** REST aggTrades backfill is prohibitively weighted (the reason the archive exists); the archive lags ~1 day, acceptable for backtesting. Live 24/7 tick capture is expensive storage and not currently justified.

**Two consequences to document, not rediscover as bugs:**
1. **Stream services read policy ONCE at startup.** `SpotAggTradeStreamService.BuildEnabledSpotSymbols` is evaluated at `ExecuteAsync` start with no live-refresh path. So the ticks eager→lazy flip only takes effect on a **process/service restart** (unlike cron collectors, which re-check per feed each cycle). The `:5210` Windows service MUST be restarted after deploy.
2. **If Andrew wants real-time capture** for any of these on a specific asset, add `"Eager": true` to that feed entry (Phase 2 mechanism) — no code change. This is the per-asset override, identical to candles.

At plan review, Andrew confirms this default (all three lazy) or names any feed/asset to keep `Eager`. **No `appsettings.json` edit is part of this plan** unless he elects an override.

## Existing interfaces you will consume (verified signatures)

```csharp
// Domain — FeedNames.cs (constants): Candles="candles", CandleExt="candle-ext",
//   FundingRate="funding-rate", MarkPrice="mark-price", OpenInterest="open-interest",
//   TakerVolume="taker-volume", Ticks="ticks", Liquidations="liquidations", ...
// Domain — AssetTypes.cs: Spot/Perpetual/Future/Equity; IsSpot(t), IsFutures(t), All.
// Domain — AssetPathConvention.DirectoryName(symbol, assetType): "{sym}_perp" for perp/future.
// Domain — MoneyConvert.ToLong(decimal) (rounds AwayFromZero).

// Domain — FeedStatus.cs (sealed CLASS, init-only props — Task 1 adds CompleteMonths):
public sealed class FeedStatus {
    public string FeedName { get; init; } = "";
    public string Interval { get; init; } = "";
    public long? FirstTimestamp { get; init; }
    public long? LastTimestamp { get; init; }
    public DateTimeOffset? LastRunUtc { get; init; }
    public long RecordCount { get; init; }
    public IReadOnlyList<DataGap> Gaps { get; init; } = [];
    public CollectionHealth Health { get; init; } = CollectionHealth.Healthy;
}
public readonly record struct DataGap { public long FromMs { get; init; } public long ToMs { get; init; } }

// Application.Archive — the classification source of truth
public sealed class ArchiveMaterializerRegistry {
    public ArchiveMaterializerRegistry(IEnumerable<IArchiveMaterializer> materializers);
    public IArchiveMaterializer? Resolve(string exchange, string feedName, string assetType);
    public bool IsReplenishable(string exchange, string feedName, string assetType);
}
public interface IArchiveMaterializer {
    string Exchange { get; }
    string FeedName { get; }
    bool Supports(string assetType);
    Task<ArchiveMonthResult> MaterializeMonth(
        AssetCollectionConfig assetConfig, FeedCollectionConfig feedConfig,
        string assetDir, int year, int month, CancellationToken ct = default);
}
public readonly record struct ArchiveMonthResult(long RowsWritten, bool AvailableAtSource);

// Application.Archive — archive client + atomic writer + CSV helpers
public interface IBinanceArchiveClient {
    Task<Stream?> DownloadMonthly(string market, string dataset, string symbol, string? interval, int year, int month, CancellationToken ct = default);
    Task<Stream?> DownloadDaily(string market, string dataset, string symbol, string? interval, DateOnly date, CancellationToken ct = default);
}   // market = "spot" | "futures/um"; file-name token = interval ?? dataset; null on 404.
public interface IPartitionFileWriter {
    Task ReplacePartition(string partitionPath, string header, IEnumerable<string> rows, CancellationToken ct = default);
}   // temp file + atomic rename over partitionPath; accepts ANY path (daily or monthly).
public static class ArchiveCsv {
    public static IEnumerable<string[]> ReadRows(TextReader reader);      // auto-skips header (first char non-digit)
    public static long NormalizeTimestampMs(long raw);                    // µs→ms when raw >= 1e14
}

// Infrastructure.Archive — shared status/gap merge helper (internal static, same project as materializers)
static class ArchiveStatusMerger {
    static Task<long> CountDataRows(string partitionPath, CancellationToken ct = default);   // lines-1, 0 if absent
    static List<DataGap> DetectGaps(List<(long Ts, string[] Row)> parsed, long intervalMs);  // jump > intervalMs
    static Task MergeStatus(IFeedStatusStore feedStatusStore, string assetDir, string feedName,
        string interval, long monthFirst, long monthLast, long recordCountDelta,
        IReadOnlyList<DataGap> newGaps, CancellationToken ct = default);   // min/max ts, additive count, dedup gaps, Health=Degraded if gaps
}

// Application.Abstractions — schema + status stores
public interface ISchemaManager {
    Task EnsureSchema(string assetDir, string feedName, string interval, string[] columns, AutoApplySpec? autoApply = null, CancellationToken ct = default);
    Task EnsureCandleConfig(string assetDir, int decimalDigits, string interval, CancellationToken ct = default);
    Task<FeedMetadata?> Load(string assetDir, CancellationToken ct = default);
}
public interface IFeedStatusStore {
    Task<FeedStatus?> Load(string assetDir, string feedName, string interval, CancellationToken ct = default);
    Task Save(string assetDir, string feedName, string interval, FeedStatus status, CancellationToken ct = default);
}   // persisted as {assetDir}/{feedName}/status[_{interval}].json, System.Text.Json camelCase.

// Application.Archive — coverage predicate (Task 2 extends the signature)
public interface IMonthCoverageCalculator {
    Task<bool> IsMonthCovered(string assetDir, string feedName, string interval,
        int year, int month, IReadOnlyList<DataGap> gaps,
        long? effectiveStartMs = null, CancellationToken ct = default);
}

// Application.HistoryLoaderOptions.cs
public sealed class FeedCollectionConfig {
    public required string Name { get; init; }
    public string Interval { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public DateOnly? HistoryStart { get; init; }
    public double GapThresholdMultiplier { get; init; } = 2.0;
    public bool Eager { get; init; }
}
public sealed class AssetCollectionConfig {
    public required string Symbol { get; init; }
    public string Exchange { get; init; } = "binance";
    public required string Type { get; init; }
    public int DecimalDigits { get; init; } = 2;
    public DateOnly HistoryStart { get; init; } = new(2020, 1, 1);
    public List<FeedCollectionConfig> Feeds { get; init; } = [];
}
public sealed class LoadOptions {   // Task 9 adds MaxTickMonthsPerRequest
    public int MaxQueueDepth { get; init; } = 16;
    public int JobRetentionMinutes { get; init; } = 30;
    public int MaxMonthsPerRequest { get; init; } = 600;
}

// WebApi.Endpoints — load validation (Task 9 extends)
internal sealed record LoadRequest(string Exchange, string Symbol, string AssetType,
    string FeedName, string Interval, DateOnly From, DateOnly To);
internal sealed record LoadValidationError(string Code, string Message);
internal static class LoadRequestValidator {
    public static bool IsKnownAssetType(string assetType);
    public static LoadValidationError? Validate(LoadRequest request, ArchiveMaterializerRegistry registry, LoadOptions options);
}

// Application — tick writer (Task 3 changes Write signature)
public readonly record struct TickResumeState(long LastAggId, long LastTsMs);
public interface ITickFeedWriter {
    void Write(string assetDir, FeedRecord record);   // Task 3 → Write(assetDir, record, decimalDigits)
    Task<TickResumeState?> ResumeFrom(string assetDir, CancellationToken ct = default);
}
public readonly record struct FeedRecord(long TimestampMs, double[] Values);  // ticks: [price, qty, is_buyer_maker, agg_id]
```

**Archive dataset facts (materializers MUST handle; verify against a live zip in the smoke, Task 14):**

- **aggTrades** → feed `ticks`. Datasets: `data/{market}/monthly|daily/aggTrades/{SYMBOL}/{SYMBOL}-aggTrades-{stamp}.zip` (no interval segment; pass `interval: null`). Columns — futures/um (7): `agg_trade_id, price, quantity, first_trade_id, last_trade_id, transact_time, is_buyer_maker`; spot (8): same + trailing `is_best_match`. Timestamp at **index 5** (`transact_time`), through `NormalizeTimestampMs` (spot µs from 2025-01). Map: `agg_id←[0]`, `price←[1]`, `qty←[2]`, `is_buyer_maker←[6]` (`true`/`false` → `1`/`0`). Header presence varies — rely on `ArchiveCsv.ReadRows`.
- **fundingRate** (monthly, futures-only) → half of feed `funding-rate`. Path `data/futures/um/monthly/fundingRate/{SYMBOL}/{SYMBOL}-fundingRate-{yyyy-MM}.zip` (`interval: null`). Columns: `calc_time, funding_interval_hours, last_funding_rate`; ts←`calc_time` (index 0, ms), rate←`last_funding_rate` (index 2).
- **markPriceKlines** `8h` → other half of `funding-rate` (`mark_price` = close). Reuse `KlinesArchiveMaterializer`'s kline column layout: `[0]=openTime, [4]=close`. Join fundingRate rows to the markPrice close whose openTime == the funding `calc_time` (8h boundaries align); when absent, carry the last-known close forward within the month.
- **klines** `{interval}` → feed `taker-volume` (derived). Column layout (12): `[5]=volume, [7]=quote_volume, [8]=count, [9]=taker_buy_volume, [10]=taker_buy_quote_volume`. Derivation: `buy_vol_usd = [10]`, `sell_vol_usd = [7] − [10]`, `ratio = buy_vol_usd / sell_vol_usd` (0 when `sell_vol_usd <= 0`). Doubles, `InvariantCulture` (NOT scaled — matches the existing `taker-volume` feed columns `["buy_vol_usd","sell_vol_usd","ratio"]`).

---

### Task 1: `FeedStatus.CompleteMonths` + status-merge support for complete months

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Domain/FeedStatus.cs` (add one property)
- Modify: `src/AlgoTradeForge.HistoryLoader.Domain/FeedNames.cs` (add `UsesMonthlyCompleteness` helper)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/ArchiveStatusMerger.cs` (add `MarkCompleteMonth` **and** preserve `CompleteMonths` in the existing `MergeStatus` rebuild — see Step 3)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/State/FeedStatusCompleteMonthsTests.cs` (create)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/ArchiveStatusMergerTests.cs` (extend if present; else create)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Domain/FeedNamesTests.cs` (create)

**Interfaces:**
- Produces: `FeedStatus.CompleteMonths : IReadOnlyList<string>` (default `[]`); `ArchiveStatusMerger.MarkCompleteMonth(IFeedStatusStore store, string assetDir, string feedName, string interval, string monthKey, CancellationToken ct)` — loads status, adds `monthKey` ("yyyy-MM") to `CompleteMonths` if absent (sorted ordinal), Saves.
- **Also patches `MergeStatus` to carry `CompleteMonths` forward.** `MergeStatus` rebuilds `FeedStatus` from scratch via an object initializer (`FeedStatus` is a `sealed class`, no `with`). It predates `CompleteMonths`, so as written it drops the field to `[]` on every save. In Tasks 4/5 the per-month sequence is `MergeStatus → MarkCompleteMonth`: within one month this self-heals (MarkCompleteMonth re-adds the current month), but **across months it wipes every prior month's marker** — a Jan→Dec backfill ends with `CompleteMonths == ["2024-12"]` only, so coverage re-materializes 11/12 months forever and Task 14 Step 3.3 fails. Fix: add `CompleteMonths = existing?.CompleteMonths ?? []` to the `MergeStatus` rebuild. **Coordinate with Task 11's M6 edit** — it touches the same rebuild (in-month gap prune); whoever lands M6 must keep this line.
- Produces: `FeedNames.UsesMonthlyCompleteness(string feedName) : bool` ⇒ `feedName is Ticks or FundingRate`. **This is the load-bearing discriminator** for the whole phase: these feeds are configured with `Interval == ""` (no fixed interval string in the partition path) and are materialized whole-month from a monthly archive zip, so their coverage is the `CompleteMonths` marker, NOT the interval row-count predicate. Every site that currently special-cases `== FeedNames.Ticks` MUST use this helper instead, so `funding-rate` (the first interval-less *replenishable* feed) is handled identically. Tasks 2, 4, 5, 9, 10 all depend on it.

> **Why a helper, not `== Ticks`:** the plan's first draft keyed empty-interval handling on the tick feed name — a proxy-discriminator bug. `funding-rate` is also interval-less (`appsettings.json` `Interval: ""`) and would otherwise hit `IntervalParser.ToTimeSpan("")` (throws) in the validator (Task 9) AND the coverage calculator (Task 2), and be invisible to the coverage endpoint's `????-??_{interval}.csv` glob (Task 10). The discriminator is *interval-less monthly-completeness coverage*, and `UsesMonthlyCompleteness` names it once. (`liquidations` is also interval-less but never replenishable, so it never reaches these paths.)

- [ ] **Step 1: Write the failing tests**

```csharp
// FeedStatusCompleteMonthsTests.cs — round-trip through the real FeedStatusManager (temp dir).
[Fact] public async Task CompleteMonths_RoundTrips_ThroughStore()
{
    // Arrange: FeedStatusManager over a temp IFileStorage; save a FeedStatus with
    //   CompleteMonths = ["2024-01","2024-02"], FeedName="ticks", Interval="".
    // Act: Load it back.
    // Assert: loaded.CompleteMonths == ["2024-01","2024-02"] (serialized as "completeMonths").
}
[Fact] public async Task LegacyStatus_WithoutCompleteMonths_LoadsAsEmpty()
{
    // Arrange: write a status.json WITHOUT the completeMonths field (older format).
    // Assert: loaded.CompleteMonths is empty (default []), no exception.
}
```

```csharp
// ArchiveStatusMergerTests.cs
[Fact] public async Task MarkCompleteMonth_Adds_WhenAbsent_SortedOrdinal()
{
    // store returns FeedStatus { CompleteMonths = ["2024-03"] }; MarkCompleteMonth(..., "2024-01")
    // → Save called with CompleteMonths == ["2024-01","2024-03"].
}
[Fact] public async Task MarkCompleteMonth_Idempotent_WhenPresent()
{
    // CompleteMonths already contains "2024-01" → Save NOT called (or called with unchanged list).
}
[Fact] public async Task MergeStatus_PreservesCompleteMonths()
{
    // The cross-month data-loss guard: store returns FeedStatus { CompleteMonths = ["2024-01"] };
    // MergeStatus(feedName="ticks", interval="", monthFirst/Last in 2024-02, delta, gaps:[])
    // → Save called with CompleteMonths STILL containing "2024-01" (not wiped by the rebuild).
    // Without the fix this asserts [] and fails — the regression this task closes.
}
```

```csharp
// FeedNamesTests.cs — the interval-less discriminator.
[Theory]
[InlineData(FeedNames.Ticks, true)]
[InlineData(FeedNames.FundingRate, true)]
[InlineData(FeedNames.Candles, false)]
[InlineData(FeedNames.TakerVolume, false)]   // taker-volume keeps interval "15m"
[InlineData(FeedNames.OpenInterest, false)]
public void UsesMonthlyCompleteness_ClassifiesIntervalLessFeeds(string feed, bool expected) =>
    Assert.Equal(expected, FeedNames.UsesMonthlyCompleteness(feed));
```

- [ ] **Step 2: Run → FAIL** (`--filter "FullyQualifiedName~CompleteMonths|FullyQualifiedName~ArchiveStatusMerger|FullyQualifiedName~FeedNames"`).

- [ ] **Step 3: Implement**

In `FeedStatus.cs`, after `Gaps`:

```csharp
    /// <summary>Months ("yyyy-MM") materialized from a complete monthly archive zip. Coverage marker for interval-less feeds (ticks, funding-rate) — spec §2.</summary>
    public IReadOnlyList<string> CompleteMonths { get; init; } = [];
```

In `FeedNames.cs`, add:

```csharp
    // Interval-less, monthly-zip-sourced feeds: coverage is the CompleteMonths marker, not the
    // row-count predicate (they carry no interval string, so IntervalParser cannot run on them).
    public static bool UsesMonthlyCompleteness(string feedName) => feedName is Ticks or FundingRate;
```

In `ArchiveStatusMerger.cs`, add (mirror the existing `MergeStatus` load/save shape):

```csharp
    public static async Task MarkCompleteMonth(
        IFeedStatusStore feedStatusStore, string assetDir, string feedName, string interval,
        string monthKey, CancellationToken ct = default)
    {
        var status = await feedStatusStore.Load(assetDir, feedName, interval, ct)
            ?? new FeedStatus { FeedName = feedName, Interval = interval };
        if (status.CompleteMonths.Contains(monthKey))
            return;
        var months = new List<string>(status.CompleteMonths) { monthKey };
        months.Sort(StringComparer.Ordinal);
        await feedStatusStore.Save(assetDir, feedName, interval,
            status with... ; // FeedStatus is a class, not a record — rebuild via object initializer:
    }
```

Note: `FeedStatus` is a `sealed class` (no `with`). Rebuild explicitly, copying every field and replacing `CompleteMonths = months`. Add the standard terse comment only if non-obvious.

**Also patch the existing `MergeStatus` rebuild** in the same file — add `CompleteMonths = existing?.CompleteMonths ?? []` to the `new FeedStatus { ... }` initializer so it stops erasing the marker on every per-month save (see Interfaces above for the failure trace). This is a one-line addition; the `MergeStatus_PreservesCompleteMonths` test is its guard. `MergeStatus` never adds months (only `MarkCompleteMonth` does) — it must only carry the existing list through unchanged.

- [ ] **Step 4: Run → PASS**, then full suite `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Domain/FeedStatus.cs \
        src/AlgoTradeForge.HistoryLoader.Domain/FeedNames.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/ArchiveStatusMerger.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/State/FeedStatusCompleteMonthsTests.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/ArchiveStatusMergerTests.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Domain/FeedNamesTests.cs
git commit -m "feat(archive): FeedStatus.CompleteMonths + MarkCompleteMonth + UsesMonthlyCompleteness (interval-less coverage marker)"
```

---

### Task 2: Tick coverage predicate in `MonthCoverageCalculator`

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Archive/IMonthCoverageCalculator.cs` (add param)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/MonthCoverageCalculator.cs` (tick branch)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Archive/ArchiveBackfillService.cs` (pass `CompleteMonths`)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CoverageEndpoints.cs` (compile-fix call site; the tick *section* is Task 10)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/MonthCoverageCalculatorTests.cs` (extend)

**Interfaces:**
- Produces: `IsMonthCovered(..., IReadOnlyList<DataGap> gaps, IReadOnlyList<string>? completeMonths = null, long? effectiveStartMs = null, CancellationToken ct = default)`. For `FeedNames.UsesMonthlyCompleteness(feedName)` (ticks AND funding-rate), coverage is `completeMonths?.Contains($"{year:D4}-{month:D2}") ?? false` — interval feeds keep the existing row-count path unchanged. The new param is inserted BEFORE `effectiveStartMs` so both remain optional; update all three call sites.

The monthly-completeness predicate is a single `UsesMonthlyCompleteness(feedName)` branch, not a polymorphic split: interval-less feeds have no interval and `IntervalParser.ToTimeSpan("")` would throw, so the row-count math cannot run for them; it is one factory-style discriminator (the classification data lives in `FeedNames.UsesMonthlyCompleteness`, Task 1), not behavior threaded through layers.

- [ ] **Step 1: Add failing tests** (extend the existing class, `TestClock` pinned `2026-07-07T00:00:00Z`):

```csharp
[Fact] public async Task Ticks_MonthInCompleteMonths_Covered()
{
    Assert.True(await _sut.IsMonthCovered(_assetDir, FeedNames.Ticks, "", 2024, 3, [],
        completeMonths: ["2024-03"]));
}
[Fact] public async Task Ticks_MonthNotInCompleteMonths_NotCovered()
{
    Assert.False(await _sut.IsMonthCovered(_assetDir, FeedNames.Ticks, "", 2024, 3, [],
        completeMonths: ["2024-02"]));
}
[Fact] public async Task Ticks_NullCompleteMonths_NotCovered()
{
    Assert.False(await _sut.IsMonthCovered(_assetDir, FeedNames.Ticks, "", 2024, 3, []));
}
[Fact] public async Task FundingRate_UsesCompleteMonths_NotRowCount()
{
    // The critical regression guard: funding-rate is interval-less. Covered iff in CompleteMonths,
    // and — crucially — IntervalParser.ToTimeSpan("") is NEVER reached (no throw).
    Assert.True(await _sut.IsMonthCovered(_assetDir, FeedNames.FundingRate, "", 2024, 3, [],
        completeMonths: ["2024-03"]));
    Assert.False(await _sut.IsMonthCovered(_assetDir, FeedNames.FundingRate, "", 2024, 4, [],
        completeMonths: ["2024-03"]));
}
[Fact] public async Task IntervalFeed_Unaffected_ByCompleteMonthsParam()
{
    // Existing candles coverage still works when completeMonths is passed (ignored for interval feeds).
    WritePartition(rows: 744); // 2024-01 1h full
    Assert.True(await _sut.IsMonthCovered(_assetDir, "candles", "1h", 2024, 1, [], completeMonths: []));
}
```

- [ ] **Step 2: Run → FAIL** (arity / tick branch absent).

- [ ] **Step 3: Implement**

`IMonthCoverageCalculator.IsMonthCovered` — add `IReadOnlyList<string>? completeMonths = null` before `effectiveStartMs`. In the impl, first line of the method (BEFORE `IntervalParser.ToTimeSpan(interval)`, which would throw for `interval == ""`):

```csharp
        if (FeedNames.UsesMonthlyCompleteness(feedName))
            return completeMonths?.Contains($"{year:D4}-{month:D2}") ?? false;
```

`ArchiveBackfillService` (line ~79): `status?.CompleteMonths` is in scope via the loaded status — pass it:

```csharp
            if (await coverage.IsMonthCovered(
                assetDir, feedConfig.Name, feedConfig.Interval, year, month,
                gaps, status?.CompleteMonths, effectiveStartMs, ct))
```

`CoverageEndpoints.BuildFeedEntry` call site: pass `status?.CompleteMonths` in the same new slot (keeps interval feeds unchanged; the tick *enumeration* is Task 10).

- [ ] **Step 4: Run → PASS**, then full suite.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Archive/IMonthCoverageCalculator.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/MonthCoverageCalculator.cs \
        src/AlgoTradeForge.HistoryLoader.Application/Archive/ArchiveBackfillService.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CoverageEndpoints.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/MonthCoverageCalculatorTests.cs
git commit -m "feat(archive): CompleteMonths-based tick coverage predicate"
```

---

### Task 3: Scale tick writes to Int64 (`DailyTickCsvWriter` + callers)

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Abstractions/ITickFeedWriter.cs` (Write signature)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/DailyTickCsvWriter.cs` (scale price/qty)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Collection/Feeds/AggTradeFeedCollector.cs` (pass DecimalDigits)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/SpotAggTradeStreamService.cs` (pass DecimalDigits)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Storage/DailyTickCsvWriterTests.cs` (update pins to scaled longs)

**Interfaces:**
- Produces: `ITickFeedWriter.Write(string assetDir, FeedRecord record, int decimalDigits)`. `DailyTickCsvWriter` scales `record.Values[0]` (price) and `record.Values[1]` (qty) via `MoneyConvert.ToLong((decimal)value * multiplier)` with `multiplier = (decimal)Math.Pow(10, decimalDigits)`; `is_buyer_maker` stays `(int)Values[2]` (0/1); `agg_id` stays `(long)Values[3]`. Header/path/dedup/`ResumeFrom` unchanged (`ts` and `agg_id` columns are untouched, so resume parsing is unaffected).

Why: the ONLY functional tick consumer, `PartitionedSourceReader.ReadTicks`, parses price/qty with `long.TryParse` — it requires scaled longs, exactly like candle OHLC. The live writer currently writes raw decimals (a latent bug: aggregation would `FormatException`). The archive materializer (Task 4) writes scaled longs; the current-month REST tail must match, or a single tick feed is unreadable. `10^DecimalDigits` is the only runtime scale in HistoryLoader (`AssetScaleContextFactory.FromDecimalDigits` → `QuantityScale = 1`); no qty-step exists to reconcile.

- [ ] **Step 1: Update the failing tests** — in `DailyTickCsvWriterTests.cs`, change the pinned rows from raw decimal to scaled long. Add a scaling case:

```csharp
[Fact] public async Task Write_ScalesPriceAndQty_ByDecimalDigits()
{
    var writer = new DailyTickCsvWriter(/* existing deps */);
    // price 50000.5, qty 0.123, is_buyer_maker 0, agg_id 100; DecimalDigits = 2
    writer.Write(_assetDir, new FeedRecord(_ts, [50000.5, 0.123, 0, 100]), decimalDigits: 2);
    var line = (await File.ReadAllLinesAsync(DailyPath(_ts)))[1];
    Assert.Equal($"{_ts},5000050,12,0,100", line); // 50000.5*100=5000050 ; 0.123*100=12 (AwayFromZero)
}
```

Update the day-boundary / dedup / multi-partition-resume tests to pass `decimalDigits` and assert scaled values. (`0.123 * 100 = 12.3 → MoneyConvert.ToLong = 12`; state the rounding in the assert comment.)

- [ ] **Step 2: Run → FAIL** (Write arity + decimal output).

- [ ] **Step 3: Implement**

`ITickFeedWriter.Write(string assetDir, FeedRecord record, int decimalDigits)`. In `DailyTickCsvWriter.Write`, before building the row:

```csharp
        var multiplier = (decimal)Math.Pow(10, decimalDigits);
        var price = MoneyConvert.ToLong((decimal)record.Values[0] * multiplier);
        var qty = MoneyConvert.ToLong((decimal)record.Values[1] * multiplier);
```

and emit `$"{record.TimestampMs},{price},{qty},{(int)record.Values[2]},{aggId}"`.

Callers pass their asset's digits:
- `AggTradeFeedCollector.CollectAsync` has `assetConfig.DecimalDigits` → `tickWriter.Write(assetDir, record, assetConfig.DecimalDigits)`.
- `SpotAggTradeStreamService` resolves the symbol's `AssetCollectionConfig` (it iterates `config.Assets`); thread `DecimalDigits` for the streamed symbol into the write call (build a `symbol → DecimalDigits` map once alongside the enabled-symbol set, or look it up per message).
- **THIRD caller — `TradeProjection` (LiveHost canonicalization replay, `Infrastructure/Canonicalization/TradeProjection.cs:15`):** the signature change breaks its compile (and there's a pinned test `ProjectionTests.cs:TradeProjection_WritesUnscaledDecimalRow` asserting the old raw-decimal output). Its fix is **Task 3b** — it is NOT mechanical (DecimalDigits has no path into the canonicalization pipeline today, and canonical `PriceScaleExp ≠ DecimalDigits`). **Task 3's build stays red until Task 3b lands; execute 3 and 3b as one reviewed unit, then run the suite.**

- [ ] **Step 4: Compile-and-test gate is JOINT with Task 3b.** After 3b, grep for any other `ITickFeedWriter.Write(` call sites and fix arity (`grep -rn "tickWriter.Write\|writer.Write" src/`), then run the full suite → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Abstractions/ITickFeedWriter.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Storage/DailyTickCsvWriter.cs \
        src/AlgoTradeForge.HistoryLoader.Application/Collection/Feeds/AggTradeFeedCollector.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Collection/SpotAggTradeStreamService.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Storage/DailyTickCsvWriterTests.cs
git commit -m "fix(ticks): store tick price/qty as scaled Int64 (Int64 Money Convention; aggregation-readable)"
```
(Commit only after Task 3b compiles — the two form one atomic signature change. If your process requires each commit to build, squash 3+3b or commit 3b immediately after.)

---

### Task 3b: Canonicalization tick writer — scaled Int64 (`TradeProjection`)

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Canonicalization/CanonicalizerOptions.cs` (add `InstrumentDecimalDigits`)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/InstrumentAssetDirMap.cs` (carry per-instrument `DecimalDigits`, or add a sibling `InstrumentScaleMap`)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/TradeProjection.cs` (rescale to Int64)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/CanonicalizerServiceCollectionExtensions.cs` (populate the map)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (source digits from `HistoryLoaderOptions.Assets` alongside the existing `AssetDirBase` PostConfigure)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/ProjectionTests.cs` (flip `TradeProjection_WritesUnscaledDecimalRow` → scaled-long expectation)

**Interfaces:**
- Consumes: `ITickFeedWriter.Write(assetDir, record, decimalDigits)` (Task 3). Produces: `TradeProjection` writes scaled Int64 tick rows identical in format to the HistoryLoader writers.

**Why this is not mechanical (verified by exploration):**
- `TradeProjection` has NO `AssetCollectionConfig` / `HistoryLoaderOptions` in scope — the canonicalization pipeline is wired from `CanonicalizerOptions` only. The single per-tick scale it currently sees is the canonical header's `PriceScaleExp` / `QtyScaleExp` (`SegmentHeader`). `DecimalDigits` must be **plumbed in**.
- Canonical `PriceScaleExp` does NOT equal `DecimalDigits` in general (independent config: relay `BinanceLiveOptions.InstrumentScales` default price-exp 2 vs. HistoryLoader per-asset digits 2/3/4/5). So you cannot write the canonical long directly; you must **rescale** `frame.Price` from `PriceScaleExp` to `DecimalDigits` (and qty from `QtyScaleExp`).

**Behavior contract:**
1. Add `Dictionary<string,int> InstrumentDecimalDigits` to `CanonicalizerOptions` (instrument → price digits), populated in `Program.cs` from `HistoryLoaderOptions.Assets` (`Symbol → DecimalDigits`) next to the existing `AssetDirBase` `PostConfigure` (Program.cs:62-66). Thread it into `InstrumentAssetDirMap` (or a sibling `InstrumentScaleMap`) so `TradeProjection` can resolve `digits` per `loc.InstrumentOrVenue`.
2. In `TradeProjection.Apply`, **keep the existing `CanonicalScale.Unscale(...)` calls** that produce the decimal price/qty magnitude, and pass `digits` to the writer: `Write(assetDir, new FeedRecord(ts, [priceMagnitude, qtyMagnitude, isBuyerMaker, seq]), digits)`. The writer's `MoneyConvert.ToLong(magnitude * 10^digits)` (Task 3) does the scaling — one code path shared with the other two callers, **no separate `Rescale` helper**. The only new work is the `instrument → digits` lookup. (This is chosen over a direct canonical-long-to-`10^digits` integer rescale because the double round-trip is exact for magnitudes ≤ 2^53 and reuses Task 3's writer verbatim; revisit only if a configured digit count exceeds that precision.)
3. If missing from the map (instrument not in `Assets`), fall back to `header.PriceScaleExp`/`QtyScaleExp` as `digits` (writes canonical-scaled long — internally consistent) and log once.

> **Simplification adopted (per step 2):** keep `CanonicalScale.Unscale` producing the decimal magnitude and pass `digits` to the writer, so the writer's `MoneyConvert.ToLong(magnitude * 10^digits)` does the scaling — one code path shared with the other two callers, no separate `Rescale`. The only new work is plumbing the `instrument → digits` lookup.

- [ ] **Step 1: Flip the pinned test** — `ProjectionTests.cs:TradeProjection_WritesUnscaledDecimalRow` → rename to `TradeProjection_WritesScaledLongRow`; with a mapped `digits: 2` the `price 5000050 @ exp2 → magnitude 50000.5 → scaled 5000050`, `qty 123 @ exp3 → magnitude 0.123 → scaled 12`; assert `lines[1] == $"{Ts},5000050,12,1,77"`. Add a case where the instrument is absent from the map → falls back to canonical exp.
- [ ] **Step 2: Run → FAIL** (compile: `Write` arity; assertion: old decimal string).
- [ ] **Step 3: Implement** the plumbing + the one-line `digits` lookup + `Write(..., digits)`.
- [ ] **Step 4: Run → PASS** + full suite + `dotnet build AlgoTradeForge.slnx` 0/0 (this is where Task 3's red build goes green).
- [ ] **Step 5: Migration note (controller, Task 14 smoke).** Existing canonicalized tick CSVs under `{DataRoot}/{exchange}/{asset}/ticks/*.csv` are raw-decimal and **already unreadable** by `PartitionedSourceReader` (pre-existing). After this change, in the sandbox smoke, **delete stale raw-decimal tick partitions and reset the `_canon-cursors`** (`CanonicalizerOptions.CursorPrefix`) so the canonicalizer re-emits scaled-long from the `.atft` segments. Note this in the Task 14 report; production remediation is the same clear-and-recanonicalize.
- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Canonicalization/CanonicalizerOptions.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/InstrumentAssetDirMap.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/TradeProjection.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Canonicalization/CanonicalizerServiceCollectionExtensions.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Canonicalization/ProjectionTests.cs
git commit -m "fix(ticks): canonicalization TradeProjection writes scaled Int64 (plumb DecimalDigits)"
```

---

### Task 4: `AggTradesArchiveMaterializer` (feed `ticks`, daily partitions)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/AggTradesArchiveMaterializer.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/ArchiveStatusMerger.cs` (`CountDataRows` → streaming)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/AggTradesArchiveMaterializerTests.cs`

**Interfaces:**
- Consumes: `IBinanceArchiveClient`, `IPartitionFileWriter`, `ISchemaManager`, `IFeedStatusStore`, `ArchiveCsv`, `ArchiveStatusMerger` (incl. `MarkCompleteMonth` from Task 1).
- Produces: `IArchiveMaterializer` for `FeedNames.Ticks`. Constructor:

```csharp
public AggTradesArchiveMaterializer(
    IBinanceArchiveClient archive, IPartitionFileWriter partitionWriter,
    ISchemaManager schemaManager, IFeedStatusStore feedStatusStore,
    ILogger<AggTradesArchiveMaterializer> logger)
```

**Behavior contract:**
1. `Exchange => "binance"`; `FeedName => FeedNames.Ticks`; `Supports(assetType) => true` (aggTrades exist for spot AND futures).
2. `MaterializeMonth`: `market = IsSpot ? "spot" : "futures/um"`. Try `DownloadMonthly(market, "aggTrades", symbol, interval: null, year, month)`. Track `bool fromMonthlyZip`. If null, assemble from `DownloadDaily(..., day)` over the month (closed month — ownership rule); `fromMonthlyZip = false`. If nothing available → `ArchiveMonthResult(0, false)`.
3. **Streaming single pass — never materialize the month.** aggTrades zips are ordered by `agg_trade_id` ascending (hence `transact_time` ascending). Iterate rows via `ArchiveCsv.ReadRows` in file order; per row: `aggId = long.Parse([0])`, `price = decimal.Parse([1])`, `qty = decimal.Parse([2])`, `ts = NormalizeTimestampMs(long.Parse([5]))`, `isBuyerMaker = ParseBool([6]) ? 1 : 0` (accept `"true"/"false"` and `"1"/"0"`; drop spot's `[7]`). **Dedup by monotonic watermark:** skip any row with `aggId <= lastSeenAggId` — the SAME assumption `DailyTickCsvWriter`'s dedup relies on (agg_ids strictly increase per symbol). **No `HashSet`, no sort** — that would defeat the streaming constraint on a ~10⁸-row month.
4. Scale price/qty to `long` = `MoneyConvert.ToLong(value * 10^DecimalDigits)` (Global "Tick encoding"). Row = `$"{ts},{priceLong},{qtyLong},{isBuyerMaker},{aggId}"`, header `ts,price,qty,is_buyer_maker,agg_id`.
5. **Buffer one UTC day at a time.** Track the current day (`DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime.Date`); accumulate scaled rows into a day buffer; when `ts` crosses into a new day, **flush** the buffer via `IPartitionFileWriter.ReplacePartition({assetDir}/ticks/{yyyy-MM-dd}.csv, header, dayRows)` and start fresh; flush the final day after the loop. Because rows are time-ordered, at most ONE day (~10⁷ rows for BTC) is in memory — never the whole month. `EnsureSchema(assetDir, FeedNames.Ticks, "", ["price","qty","is_buyer_maker","agg_id"], ct: ct)` once, before the loop.
6. Status: `ArchiveStatusMerger.MergeStatus(feedStatusStore, assetDir, FeedNames.Ticks, "", firstTs, lastTs, rowsWritten - previousRowsForMonth, gaps: [], ct)`. Ticks have no fixed cadence → do NOT synthesize gaps (empty). `previousRowsForMonth` = sum of the month's existing daily files' data-row counts before replacement. **`ArchiveStatusMerger.CountDataRows` must stream** — today it calls `File.ReadAllLinesAsync`, which materializes multi-million-row tick files as `string[]`; change it to a `StreamReader.ReadLineAsync` loop (mirror `MonthCoverageCalculator.CountDataRows`) so every materializer benefits. (`RecordCount` stays approximate across re-materialization — coverage math reads files, not `RecordCount`.)
7. **`CompleteMonths` marker (spec §2):** ONLY when `fromMonthlyZip` is true, call `ArchiveStatusMerger.MarkCompleteMonth(feedStatusStore, assetDir, FeedNames.Ticks, "", $"{year:D4}-{month:D2}", ct)`. A month assembled from daily zips is NOT complete-by-construction and gets no marker (so coverage will re-check it).
8. Return `ArchiveMonthResult(rowsWritten, true)`.

- [ ] **Step 1: Failing tests** (NSubstitute `IBinanceArchiveClient` returning `MemoryStream` CSV fixtures; real `PartitionFileWriter` into a temp dir; NSubstitute `ISchemaManager`; real or captured `IFeedStatusStore`):

Fixtures:
```csharp
// futures/um aggTrades, 7 cols, header present. Two trades same day, one next day.
private const string FuturesAgg =
    "agg_trade_id,price,quantity,first_trade_id,last_trade_id,transact_time,is_buyer_maker\n" +
    "100,50000.5,0.100,1,1,1709251200000,true\n" +   // 2024-03-01
    "101,50001.0,0.200,2,2,1709251260000,false\n" +   // 2024-03-01
    "102,50002.0,0.050,3,3,1709337600000,true\n";     // 2024-03-02
// spot aggTrades, 8 cols, µs timestamps (2025+), trailing is_best_match.
private const string SpotAggMicros =
    "1000,60000.00,0.010,10,10,1735689600000000,false,true\n"; // 2025-01-01 00:00 µs
```

Tests:
1. `MaterializeMonth_Futures_SplitsByDay_ScaledLongs` — DecimalDigits=2, monthly zip returns `FuturesAgg` → `ticks/2024-03-01.csv` has 2 rows `1709251200000,5000050,10,0,100` and `1709251260000,5000100,20,1,101`; `ticks/2024-03-02.csv` has `1709337600000,5000200,5,0,102`; header `ts,price,qty,is_buyer_maker,agg_id`.
2. `MaterializeMonth_Spot_Microseconds_Normalized` — spot zip (`market == "spot"`), µs ts normalized to ms; `is_best_match` column dropped; file `ticks/2025-01-01.csv`.
3. `MaterializeMonth_FromMonthlyZip_MarksCompleteMonth` — monthly zip present → `MarkCompleteMonth("2024-03")` observed on the status store.
4. `MaterializeMonth_AssembledFromDailies_DoesNotMarkComplete` — monthly null, dailies return per-day CSV → files written but NO `CompleteMonths` entry.
5. `MaterializeMonth_DedupsByAggId` — a duplicated agg_id row → written once.
6. `MaterializeMonth_NothingAtSource_ReportsUnavailable` — all downloads null → `AvailableAtSource == false`, no files.

- [ ] **Step 2: Run → FAIL.**
- [ ] **Step 3: Implement** per contract (structure mirrors `KlinesArchiveMaterializer`; the streaming day-flush loop + monotonic-watermark dedup + scaled-long rows + `MarkCompleteMonth` are the novel pieces). Add a terse `// aggTrades are GB-scale; stream + flush per UTC day, never buffer the month` note on the loop. Also make `ArchiveStatusMerger.CountDataRows` stream (per Files) and add an `ArchiveStatusMergerTests` case proving a large file is counted without `ReadAllLines`.
- [ ] **Step 4: Run → PASS**, then full suite.
- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/AggTradesArchiveMaterializer.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/ArchiveStatusMerger.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/AggTradesArchiveMaterializerTests.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/ArchiveStatusMergerTests.cs
git commit -m "feat(archive): AggTradesArchiveMaterializer (ticks; streaming daily partitions, scaled longs, CompleteMonths)"
```

---

### Task 5: `FundingRateArchiveMaterializer` (feed `funding-rate`, fundingRate ⋈ markPriceKlines)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/FundingRateArchiveMaterializer.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/FundingRateArchiveMaterializerTests.cs`

**Interfaces:**
- Produces: `IArchiveMaterializer` for `FeedNames.FundingRate`. Constructor: same five deps as Task 4.

**Behavior contract:**
1. `Exchange => "binance"`; `FeedName => FeedNames.FundingRate`; `Supports => AssetTypes.IsFutures(assetType)` (futures-only).
2. Feed interval is `""` → partition path `{assetDir}/funding-rate/{yyyy-MM}.csv` (no interval suffix). Columns `["rate","mark_price"]`; `EnsureSchema(assetDir, FeedNames.FundingRate, "", ["rate","mark_price"], autoApply: new AutoApplySpec("FundingRate", "rate"), ct)` — the AutoApplySpec matches the live collector so funding cash-flows still auto-apply.
3. Download `fundingRate` **monthly** (`interval: null`); track `bool fromMonthlyZip`. Parse rows: `ts = NormalizeTimestampMs(long.Parse([0]))` (`calc_time`), `rate = double.Parse([2])` (`last_funding_rate`). Keep in-month, order by ts. If monthly unavailable → assemble from daily fundingRate zips (`fromMonthlyZip = false`); if still nothing → `ArchiveMonthResult(0, false)`.
4. Download `markPriceKlines` **`8h`** for the same month (monthly, else daily). Build `Dictionary<long, double>` of `openTime → close` (kline `[0]`→ts via `NormalizeTimestampMs`, `[4]`→close). For each funding row, `mark_price = map.TryGetValue(ts) ?? lastKnownClose` (carry forward within the month; **0.0 before the first close — logged once; document in the schema note that auto-apply consumers must tolerate a leading 0.0 mark_price**). Accepted approximation (spec §2: mark ≈ 8h-boundary close).
5. Row = `$"{ts},{rate.ToString(InvariantCulture)},{markPrice.ToString(InvariantCulture)}"` (doubles, unscaled — funding rate is dimensionless, mark_price stays a market price double consistent with the live feed). Partition path `{assetDir}/funding-rate/{yyyy-MM}.csv` (interval-less, NO `_{interval}` suffix). One partition via `ReplacePartition`.
6. Status + coverage: `MergeStatus(feedStatusStore, assetDir, FeedNames.FundingRate, "", firstTs, lastTs, rows - previousRows, ArchiveStatusMerger.DetectGaps(parsed, 8h), ct)` (gaps informational — funding has an 8h cadence — but coverage does NOT use them). **Expect `Health == Degraded` on a fully-covered funding month** whenever `DetectGaps`/carry-forward yields any gap — harmless, because the coverage predicate reads `CompleteMonths`, not `Health` (same for ticks). Do not "fix" it by suppressing gaps. **Coverage is the `CompleteMonths` marker** (funding is interval-less, `UsesMonthlyCompleteness` true): when `fromMonthlyZip` is true, call `ArchiveStatusMerger.MarkCompleteMonth(feedStatusStore, assetDir, FeedNames.FundingRate, "", $"{year:D4}-{month:D2}", ct)`. A month assembled from daily zips gets no marker (re-checked next run) — identical semantics to ticks (Task 4).
7. Return `ArchiveMonthResult(rows, true)`.

- [ ] **Step 1: Failing tests** — fixtures:

```csharp
private const string FundingCsv =
    "calc_time,funding_interval_hours,last_funding_rate\n" +
    "1709251200000,8,0.00010000\n" +   // 2024-03-01 00:00
    "1709280000000,8,0.00012000\n";    // 2024-03-01 08:00
private const string MarkKlines8h =    // markPriceKlines 8h: openTime,o,h,l,close,...
    "1709251200000,50000,50100,49900,50050,0,...\n" +
    "1709280000000,50050,50200,50000,50150,0,...\n";
```

Tests:
1. `MaterializeMonth_JoinsMarkPriceClose_OnFundingBoundary` — funding-rate/2024-03.csv rows `1709251200000,0.0001,50050` and `1709280000000,0.00012,50150`.
2. `MaterializeMonth_MissingMarkClose_CarriesForward` — drop the second mark kline → second row uses `50050` (carried).
3. `MaterializeMonth_RejectsSpot` — `Supports(AssetTypes.Spot)` false.
4. `MaterializeMonth_NoFundingAtSource_ReportsUnavailable`.
5. `MaterializeMonth_EnsuresAutoApplySpec` — `ISchemaManager.EnsureSchema` received `AutoApplySpec("FundingRate","rate")`.
6. `MaterializeMonth_FromMonthlyZip_MarksCompleteMonth` — monthly fundingRate zip present → `MarkCompleteMonth("2024-03")` observed; the assembled-from-dailies case marks NOTHING. Partition written at `funding-rate/2024-03.csv` (no interval suffix).

- [ ] **Step 2: Run → FAIL.** — [ ] **Step 3: Implement** (two-download join is the novel piece; verify the fundingRate archive column order against a live zip in Task 14 and adjust indices if Binance differs). — [ ] **Step 4: Run → PASS** + full suite.
- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/FundingRateArchiveMaterializer.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/FundingRateArchiveMaterializerTests.cs
git commit -m "feat(archive): FundingRateArchiveMaterializer (fundingRate join markPriceKlines close)"
```

---

### Task 6: `TakerVolumeArchiveMaterializer` (feed `taker-volume`, derived from klines)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/TakerVolumeArchiveMaterializer.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/TakerVolumeArchiveMaterializerTests.cs`

**Interfaces:**
- Produces: `IArchiveMaterializer` for `FeedNames.TakerVolume`. Constructor: same five deps.

**Behavior contract:**
1. `Exchange => "binance"`; `FeedName => FeedNames.TakerVolume`; `Supports => AssetTypes.IsFutures(assetType)` (futures-only; matches the deleted collector).
2. Download `klines` for `feedConfig.Interval` (monthly, else daily) — same source/parse as `KlinesArchiveMaterializer`. Kline columns: `[5]=volume, [7]=quote_volume, [9]=taker_buy_volume, [10]=taker_buy_quote_volume`.
3. Per row: `buy_vol_usd = [10]` (taker buy quote), `sell_vol_usd = [7] - [10]` (total quote − taker buy quote = taker sell quote), `ratio = sell_vol_usd > 0 ? buy_vol_usd / sell_vol_usd : 0`. Doubles, `InvariantCulture`. Columns `["buy_vol_usd","sell_vol_usd","ratio"]` (byte-identical to the retired live feed — existing backtest configs keep reading it). NOT scaled.
4. Partition `{assetDir}/taker-volume/{yyyy-MM}_{interval}.csv`; `EnsureSchema(assetDir, FeedNames.TakerVolume, interval, [...], ct)`; gaps via `DetectGaps` with `intervalMs = IntervalParser.ToTimeSpan(interval)`; `MergeStatus` with delta. No `CompleteMonths`.
5. Return `ArchiveMonthResult(rows, true)`.

**Design note (do not "improve"):** this materializer re-parses the kline archive rather than reading the on-disk `candle-ext` CSV. Reason: `candle-ext` is written only alongside candles and only for futures; reading it would couple `taker-volume` materialization to candle materialization ordering. Archive downloads are free/unmetered, so re-parsing klines is the robust, dependency-free choice. The derivation columns (`taker_buy_quote_volume`, `quote_volume`) are the same kline columns `candle-ext` itself projects — "derivable from candle-ext" (spec) refers to the shared *source columns*, not the CSV file.

- [ ] **Step 1: Failing tests** — reuse the `KlineCsv` fixture shape from `KlinesArchiveMaterializerTests`:

```csharp
// row: openTime,o,h,l,c,vol,closeTime,quote_vol,count,taker_buy_vol,taker_buy_quote_vol,ignore
private const string KlineCsv =
    "1709251200000,50000,50100,49900,50050,12.5,1709254799999,625000,1500,6.25,375000,0\n";
```

Tests:
1. `MaterializeMonth_DerivesTakerVolumeColumns` — taker-volume/2024-03_15m.csv row `1709251200000,375000,250000,1.5` (buy=375000; sell=625000−375000=250000; ratio=1.5); header `ts,buy_vol_usd,sell_vol_usd,ratio`.
2. `MaterializeMonth_ZeroSellVolume_RatioZero` — quote_vol == taker_buy_quote_vol → sell 0 → ratio 0.
3. `MaterializeMonth_RejectsSpot`.
4. `MaterializeMonth_NothingAtSource_ReportsUnavailable`.

- [ ] **Step 2: Run → FAIL.** — [ ] **Step 3: Implement** (mechanical; mirror `KlinesArchiveMaterializer` download/parse, swap the row builder). — [ ] **Step 4: Run → PASS** + full suite.
- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/TakerVolumeArchiveMaterializer.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/TakerVolumeArchiveMaterializerTests.cs
git commit -m "feat(archive): TakerVolumeArchiveMaterializer (derived from kline taker columns)"
```

---

### Task 7: Retire the live `taker-volume` REST collector

**Files:**
- Delete: `src/AlgoTradeForge.HistoryLoader.Application/Collection/Feeds/TakerVolumeFeedCollector.cs`
- Delete: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Binance/BinanceFuturesClient.TakerVolume.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (remove line 78 registration)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/RatioCollectorService.cs` (drop `FeedNames.TakerVolume`)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs` (remove the taker-volume `IFeedFetcher` keyed registration, lines ~111–114)
- Test: adjust any test referencing `TakerVolumeFeedCollector` / the taker-volume fetcher.

**The `taker-volume` feed id and its on-disk partitions survive** — only the *live REST source* is removed; the Task 6 materializer replaces it. Existing backtest configs referencing `taker-volume` are untouched.

- [ ] **Step 1:** Delete the two files. `RatioCollectorService.cs` becomes:

```csharp
    protected override string[] CollectedFeedNames =>
        [FeedNames.LsRatioGlobal, FeedNames.LsRatioTopAccounts];
```

Remove `Program.cs:78` (`AddSingleton<IFeedCollector, TakerVolumeFeedCollector>()`) and the `DependencyInjection.cs` keyed `IFeedFetcher` block for `$"{futuresKey}:{FeedNames.TakerVolume}"`. Grep `grep -rn "TakerVolumeFeedCollector\|FetchTakerVolume" src/ tests/` and remove/adjust every hit (including the `BinanceFuturesClient` partial method if any caller remains).

- [ ] **Step 2: Build** `dotnet build AlgoTradeForge.slnx` → 0/0 (catches dangling references).
- [ ] **Step 3: Run** full HistoryLoader suite → PASS (a `RatioCollectorService` test asserting three feeds must drop to two; fix it here).
- [ ] **Step 4: Commit**

```bash
git add -u src/AlgoTradeForge.HistoryLoader.Application/Collection/Feeds/TakerVolumeFeedCollector.cs \
           src/AlgoTradeForge.HistoryLoader.Infrastructure/Binance/BinanceFuturesClient.TakerVolume.cs
git add src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Collection/RatioCollectorService.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs \
        tests/... # any adjusted test files
git commit -m "refactor(archive): retire live taker-volume REST collector (materializer-sourced now)"
```

---

### Task 8: Register the three new materializers (classification flip)

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs` (append to the materializer set, lines ~184–226)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/BinanceClassificationTests.cs` (extend the classification assertions)

**Interfaces:** none new — this is the composition site that makes ticks / funding-rate / taker-volume replenishable.

- [ ] **Step 1: Add failing classification tests** (mirror the existing `BinanceClassificationTests` that build the REAL materializer set):

```csharp
[Fact] public void Ticks_Spot_Replenishable()          // AggTrades supports spot
[Fact] public void Ticks_Perpetual_Replenishable()
[Fact] public void FundingRate_Perpetual_Replenishable()
[Fact] public void FundingRate_Spot_NotReplenishable() // futures-only
[Fact] public void TakerVolume_Perpetual_Replenishable()
[Fact] public void TakerVolume_Spot_NotReplenishable()
```

- [ ] **Step 2: Run → FAIL** (materializers not registered).

- [ ] **Step 3: Implement** — after the six existing registrations:

```csharp
        services.AddSingleton<IArchiveMaterializer>(sp => new AggTradesArchiveMaterializer(
            sp.GetRequiredService<IBinanceArchiveClient>(), sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(), sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<AggTradesArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new FundingRateArchiveMaterializer(
            sp.GetRequiredService<IBinanceArchiveClient>(), sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(), sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<FundingRateArchiveMaterializer>>()));
        services.AddSingleton<IArchiveMaterializer>(sp => new TakerVolumeArchiveMaterializer(
            sp.GetRequiredService<IBinanceArchiveClient>(), sp.GetRequiredService<IPartitionFileWriter>(),
            sp.GetRequiredService<ISchemaManager>(), sp.GetRequiredService<IFeedStatusStore>(),
            sp.GetRequiredService<ILogger<TakerVolumeArchiveMaterializer>>()));
```

- [ ] **Step 4: Run** the classification tests + the Phase-1 `WebApiCompositionSmokeTests` (the host must still compose) → PASS; full suite.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/DependencyInjection.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/BinanceClassificationTests.cs
git commit -m "feat(archive): register ticks/funding-rate/taker-volume materializers (classification flip → lazy)"
```

---

### Task 9: Tick disk-budget guard + tick interval special-case in `LoadRequestValidator`

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs` (`LoadOptions.MaxTickMonthsPerRequest`)
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptionsValidator.cs` (validate `> 0`)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/LoadRequestValidator.cs` (tick interval bypass + size guard)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/LoadEndpointValidationTests.cs` (extend)

**Interfaces:**
- Produces: `LoadOptions.MaxTickMonthsPerRequest : int = 24`. New 422 code `tick_load_too_large`. Interval-less feeds (`FeedNames.UsesMonthlyCompleteness` — ticks AND funding-rate, `Interval == ""`) bypass the `IntervalParser` check; the disk-cap stays **ticks-only** (funding is small).

**Note on "months × symbols":** a `/api/v1/loads` request is **single-symbol** (`LoadRequest.Symbol` is one string; there is no batch path). So the product reduces to a per-request tick-**months** cap. The option is named `MaxTickMonthsPerRequest` to say what it actually bounds; if multi-symbol batching is ever added, the guard generalizes to `months × symbols` at that point. Document this in the option's comment.

- [ ] **Step 1: Failing tests** (validator-level, matching the existing `TooManyMonths` template; a tick materializer must be in the fixture registry so the request passes `not_replenishable` first — add an `AggTradesArchiveMaterializer` to a `RegistryWithTicks()` helper, or reuse the real registry):

```csharp
[Fact] public void Ticks_OverCap_Returns_TickLoadTooLarge()
{
    var opts = new LoadOptions { MaxTickMonthsPerRequest = 6, MaxMonthsPerRequest = 600 };
    var req = ValidRequest() with {
        AssetType = AssetTypes.Perpetual, FeedName = FeedNames.Ticks, Interval = "",
        From = new DateOnly(2024, 1, 1), To = new DateOnly(2024, 12, 31) }; // 12 months
    var err = LoadRequestValidator.Validate(req, RegistryWithTicks(), opts);
    Assert.Equal("tick_load_too_large", err!.Code);
}
[Fact] public void Ticks_WithinCap_Passes()
{
    var opts = new LoadOptions { MaxTickMonthsPerRequest = 24, MaxMonthsPerRequest = 600 };
    var req = ValidRequest() with { AssetType = AssetTypes.Perpetual, FeedName = FeedNames.Ticks,
        Interval = "", From = new DateOnly(2024,1,1), To = new DateOnly(2024,3,31) };
    Assert.Null(LoadRequestValidator.Validate(req, RegistryWithTicks(), opts));
}
[Fact] public void Ticks_EmptyInterval_NotRejectedAsInvalidInterval()
{
    // Regression: IntervalParser.ToTimeSpan("") must NOT be reached for ticks.
    var req = ValidRequest() with { AssetType = AssetTypes.Perpetual, FeedName = FeedNames.Ticks, Interval = "" };
    var err = LoadRequestValidator.Validate(req, RegistryWithTicks(), new LoadOptions());
    Assert.True(err is null || err.Code != "invalid_interval");
}
[Fact] public void NonTickFeed_EmptyInterval_StillInvalidInterval()
{
    var req = ValidRequest() with { FeedName = FeedNames.Candles, Interval = "" };
    Assert.Equal("invalid_interval", LoadRequestValidator.Validate(req, RegistryWithCandles(), new LoadOptions())!.Code);
}
[Fact] public void FundingRate_EmptyInterval_Passes_AndIsNotCappedAsTick()
{
    // funding-rate is interval-less (bypasses IntervalParser) BUT is NOT subject to the tick cap.
    var req = ValidRequest() with { AssetType = AssetTypes.Perpetual, FeedName = FeedNames.FundingRate,
        Interval = "", From = new DateOnly(2020, 1, 1), To = new DateOnly(2024, 12, 31) }; // 60 months
    var err = LoadRequestValidator.Validate(req, RegistryWithFunding(),
        new LoadOptions { MaxTickMonthsPerRequest = 24 });
    Assert.Null(err); // neither invalid_interval nor tick_load_too_large
}
```
(`RegistryWithTicks()` / `RegistryWithFunding()` add the respective materializer so the request clears `not_replenishable` before reaching the interval/cap checks — model on the existing `RegistryWithCandles()` helper.)

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement**

`LoadOptions`: `public int MaxTickMonthsPerRequest { get; init; } = 24;` with a one-line comment noting the single-symbol product reduction.

`HistoryLoaderOptionsValidator` (after the `Aggregator.*` block):
```csharp
        if (options.Load.MaxTickMonthsPerRequest <= 0)
            failures.Add("Load.MaxTickMonthsPerRequest must be greater than 0.");
```

`LoadRequestValidator.Validate` — gate the interval parse and add the size guard after `not_replenishable`:
```csharp
        var intervalLess = FeedNames.UsesMonthlyCompleteness(request.FeedName); // ticks + funding-rate
        if (!intervalLess)  // interval-less feeds carry no interval; IntervalParser.ToTimeSpan("") throws
        {
            try { IntervalParser.ToTimeSpan(request.Interval); }
            catch (ArgumentException) { return new("invalid_interval", $"Unsupported interval '{request.Interval}'."); }
        }

        if (!registry.IsReplenishable(request.Exchange, request.FeedName, request.AssetType))
            return new("not_replenishable", $"Feed '{request.FeedName}' is not replenishable ...");

        if (request.FeedName == FeedNames.Ticks && months > options.MaxTickMonthsPerRequest)
            return new("tick_load_too_large",
                $"Tick load spans {months} months; limit is {options.MaxTickMonthsPerRequest} " +
                "(tick data is GB-scale — raise HistoryLoader:Load:MaxTickMonthsPerRequest to override).");
        return null;
```
(`months` is already computed above for `too_many_months` — reuse it. The disk cap is `FeedName == Ticks`, NOT `intervalLess` — funding-rate is small and must not be capped at 24.)

- [ ] **Step 4: Run → PASS** + full suite.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs \
        src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptionsValidator.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/LoadRequestValidator.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/LoadEndpointValidationTests.cs
git commit -m "feat(archive): tick disk-budget guard (MaxTickMonthsPerRequest=24) + tick empty-interval bypass"
```

---

### Task 10: Coverage endpoint — interval-less feeds section (`CompleteMonths` projection)

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CoverageEndpoints.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/CoverageEndpointTests.cs` (extend)

**Interfaces:** wire response unchanged in shape — for each interval-less feed (`FeedNames.UsesMonthlyCompleteness` — ticks AND funding-rate) present on disk, an entry `{ feed_name, interval: "", covered_months: [...], first_timestamp, last_timestamp }`, `covered_months` sourced from `FeedStatus.CompleteMonths` (the existing `????-??_{interval}.csv` glob skips these — funding partitions are `{yyyy-MM}.csv`, tick partitions are daily). **Omit behavior (pinned wire contract, FE depends on it):** when the feed dir is absent OR its status is null → NO entry (consistent with `BuildFeedEntry` returning null for a missing interval-feed dir).

- [ ] **Step 1: Failing test**

```csharp
[Theory]
[InlineData(FeedNames.Ticks)]
[InlineData(FeedNames.FundingRate)]
public async Task Coverage_IntervalLessFeed_ReportsCompleteMonths(string feed)
{
    // Arrange: manifest/dir with `feed`; feedStatusStore.Load(feed,"") returns
    //   FeedStatus { CompleteMonths = ["2024-01","2024-02"], FirstTimestamp = X, LastTimestamp = Y }.
    // Act: GetCoverage → 200.
    // Assert: feeds array contains { feed_name: feed, interval:"",
    //   covered_months:["2024-01","2024-02"], first_timestamp:X, last_timestamp:Y }.
}
[Fact] public async Task Coverage_NoIntervalLessStatus_OmitsEntry()
{
    // ticks/funding dir absent OR status null → NO entry (pinned: omit, not empty covered_months).
}
```

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Implement** — in `GetCoverage`, after the interval-feed loops, add a section over the interval-less feeds:

```csharp
        foreach (var feed in new[] { FeedNames.Ticks, FeedNames.FundingRate }) // UsesMonthlyCompleteness set
        {
            var status = await feedStatusStore.Load(assetDir, feed, "", ct);
            if (!Directory.Exists(Path.Combine(assetDir, feed)) || status is null)
                continue; // omit — pinned wire contract
            feedEntries.Add(new
            {
                feed_name = feed, interval = "",
                covered_months = status.CompleteMonths.OrderBy(m => m, StringComparer.Ordinal).ToArray(),
                first_timestamp = status.FirstTimestamp,
                last_timestamp = status.LastTimestamp,
            });
        }
```

(If a `FeedNames.MonthlyCompletenessFeeds` readonly array is cleaner than the inline literal, add it alongside `UsesMonthlyCompleteness` in Task 1 and iterate it here.)

- [ ] **Step 4: Run → PASS** + full suite. — [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CoverageEndpoints.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/CoverageEndpointTests.cs
git commit -m "feat(archive): coverage endpoint reports ticks + funding-rate CompleteMonths"
```

---

### Task 11: M6/M7 hardening — replace-guard, in-month gap prune, candle-ext shadow

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/ArchiveStatusMerger.cs` (prune stale in-month gaps)
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/KlinesArchiveMaterializer.cs` and/or `MetricsArchiveMaterializer.cs` (replace-guard) — apply where a re-materialized month could carry fewer rows than the existing partition
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CoverageEndpoints.cs` (candle-ext shadow, M7)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/ArchiveStatusMergerTests.cs` + `MonthCoverageCalculatorTests.cs`

These are the Phase 2 fable round-3 carryovers explicitly deferred to Phase 3.

**M6 — in-month gap prune on replace (fable round-3 item (i)).** This edits the same `MergeStatus` rebuild that Task 1 patched to carry `CompleteMonths` forward — **preserve that `CompleteMonths = existing?.CompleteMonths ?? []` line** when you touch the initializer. `MergeStatus` today appends `newGaps` and dedups only by exact `(FromMs, ToMs)`. A stale STREAMING gap whose slots are now present in the freshly materialized archive month is double-counted (its slots count in both `actualRows` AND gap-credit), which can falsely mark an incomplete month covered. Fix: in `MergeStatus`, before merging, **drop any existing gap fully inside `[monthFirst, monthLast]`** (the archive just rewrote that whole span atomically, so its authoritative gaps are the `newGaps` argument). Keep gaps outside the touched month untouched.

**M6 — replace-guard.** When a re-materialization would write **fewer** data rows than the existing partition already has for that month (`newRowCount < previousRows`), do NOT replace — a sparse/partial archive month must not clobber a fuller REST-collected one. Implement as a guard in the materializers around the `ReplacePartition` call (compare against `ArchiveStatusMerger.CountDataRows(path)` already computed for the status delta), logging a warning, **skipping the `MergeStatus` call**, and returning `ArchiveMonthResult(0, AvailableAtSource: true)` — `RowsWritten == 0` because this pass wrote nothing (returning `previousRows` would double-count in any caller summing deltas). Ticks are exempt (daily files; monthly zip is authoritative and complete-by-construction).

**M7 — candle-ext coverage shadow.** `candle-ext` has no materializer (it is a side-output of candles) → `registry.IsReplenishable(candle-ext)` is false, so the load path never targets it. But the coverage endpoint enumerates it as a declared interval feed and would show it "uncovered" whenever a partial partition exists, with no way to load it. Fix: in `CoverageEndpoints`, when building the `candle-ext` entry, **mirror the `candles` coverage for the same interval** (a month is covered for candle-ext iff it is covered for candles) — candle-ext is always written in tandem. Keep it a thin projection.

- [ ] **Step 1: Failing tests**
  1. `MergeStatus_PrunesInMonthGap_WhenArchiveRewritesMonth` — existing status has a gap inside 2024-03; `MergeStatus` for 2024-03 with `newGaps: []` → saved `Gaps` no longer contains the in-month gap; a gap in 2024-02 survives.
  2. `Materializer_DoesNotReplace_WhenNewRowsFewer` — existing partition 744 rows; archive month yields 700 → file unchanged (assert file mtime/content stable), result reports `AvailableAtSource: true`.
  3. `Coverage_CandleExt_MirrorsCandles` — candles 2024-03 covered, candle-ext partition partial → coverage reports candle-ext 2024-03 as covered (shadowed).
- [ ] **Step 2: Run → FAIL.** — [ ] **Step 3: Implement** the three guards. — [ ] **Step 4: Run → PASS** + full suite.
- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/ArchiveStatusMerger.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/KlinesArchiveMaterializer.cs \
        src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/MetricsArchiveMaterializer.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CoverageEndpoints.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/ArchiveStatusMergerTests.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/MonthCoverageCalculatorTests.cs
git commit -m "fix(archive): in-month gap prune + replace-guard (M6) + candle-ext coverage shadow (M7)"
```

---

### Task 12: Frontend — extend `ARCHIVE_FEEDS`; ticks empty-interval special-case

**Files:**
- Modify: `frontend/components/features/data/archive-load-form.tsx` (`ARCHIVE_FEEDS` + submit gating)
- Modify: `frontend/lib/data/coverage-mapping.ts` (Tick → coverage key, no longer `null`)
- Test: `frontend/components/features/data/archive-load-form.test.tsx`, `frontend/lib/data/coverage-mapping.test.ts`

**Interfaces:**
- The FE `ARCHIVE_FEEDS` mirror is hand-kept in lockstep with the server materializer set (server `not_replenishable`/`invalid_interval` 422s remain the source of truth — surface their `message`). Add `funding-rate`, `taker-volume`, and `ticks`. Ticks is the special case: no interval.

- [ ] **Step 1: Failing tests** (Vitest; `vi.mock('@/lib/services/data-api')`):
  1. `submits_ticks_with_empty_interval` — pick feed "Ticks", asset type perpetual, month range → `postLoad` called with `{ ..., feed_name: "ticks", interval: "", from: "2024-01-01", to: "2024-02-29" }`.
  2. `ticks_feed_is_submittable_without_interval_select` — the interval `<select>` is hidden/disabled for ticks yet `canSubmit` is true once symbol+range are set.
  3. `funding_rate_and_taker_volume_selectable` — both appear for perpetual, absent for spot (funding/taker are futures-only).
  4. `coverage-mapping.test.ts`: `Tick` feed kind → `{ feedName: "ticks", interval: "" }` (was `null`).

- [ ] **Step 2: Run → FAIL** (`npm test -- archive-load-form coverage-mapping`).

- [ ] **Step 3: Implement**

Append to `ARCHIVE_FEEDS` (add an `allowEmptyInterval?: boolean` field to the entry shape):
```tsx
  { feedName: "ticks", label: "Ticks (aggTrades)", intervals: [], assetTypes: ["spot", "perpetual"], allowEmptyInterval: true },
  { feedName: "funding-rate", label: "Funding rate", intervals: [""], assetTypes: ["perpetual"], allowEmptyInterval: true },
  { feedName: "taker-volume", label: "Taker volume", intervals: ["15m"], assetTypes: ["perpetual"] },
```

> **Deliberate FE narrowing:** the backend `Supports` for funding/taker is `AssetTypes.IsFutures` (perpetual AND future), but the form's `assetType` select only offers `spot | perpetual` (no `future`), so `["perpetual"]` is the correct mirror for the UI today. Add a `// backend Supports == IsFutures; FE offers only spot|perpetual, so perpetual mirrors it` comment so a future `future` option isn't forgotten. The server `not_replenishable` 422 remains the source of truth.
Adjust `handleFeedChange` (empty `intervals` → `interval = ""`), `canSubmit` (`selectedFeed?.allowEmptyInterval || !!interval`), and the interval `<select>` render (hide/disable when `allowEmptyInterval`). Request build already sends `interval` verbatim, so ticks/funding send `""`.

`coverage-mapping.ts`: `case "Tick": return { feedName: "ticks", interval: "" };` (removes the `null` fall-through for ticks; AltBar/aggregated stay `null`).

- [ ] **Step 4: Run → PASS**, then full `npm test`.

- [ ] **Step 5: Commit**

```bash
git add frontend/components/features/data/archive-load-form.tsx \
        frontend/components/features/data/archive-load-form.test.tsx \
        frontend/lib/data/coverage-mapping.ts frontend/lib/data/coverage-mapping.test.ts
git commit -m "feat(frontend): archive-load form supports ticks/funding-rate/taker-volume (ticks empty-interval)"
```

---

### Task 13: Docs — vision-doc §HL@cloud + restart/policy note (controller-committed)

**Files:**
- Modify: `docs/service-decomposition-vision.md` (§HL@cloud un-backfillable list + Eager↔cloud linkage)
- Modify: `docs/superpowers/specs/2026-07-07-binance-archive-backfill-design.md` (Decisions log: Phase 3 entries)

No tests. **Committed by the controller**, not an implementer (docs-staging rule).

- [ ] **Step 1:** In `docs/service-decomposition-vision.md` §HL@cloud, replace the stale "24/7 collectors for un-backfillable feeds (OI, ratios, taker vol, liquidations, funding)" line: under the post-audit classification, **only `liquidations` + spot `book-ticker` are un-backfillable**; OI, ratios, taker-volume, and funding-rate are archive-replenishable and default lazy. Record the **Eager↔cloud-profile linkage**: the archive lags ~1 day, so the cloud instance keeps warm-up-critical feeds (klines; optionally ticks/funding) `Eager:true` for live warm-up — the per-feed override is the mechanism.
- [ ] **Step 2:** Append to the spec Decisions log: spot-1s dropped from Phase 3 (rationale); tick encoding = scaled-long `10^DecimalDigits` (both writers); tick coverage = `CompleteMonths` (monthly-zip-only); `MaxTickMonthsPerRequest` default 24; the three feeds flip to lazy on materializer registration (restart required for stream feeds).
- [ ] **Step 3: Controller commits** both doc edits in one docs commit (with the standard trailers).

---

### Task 14: Whole-branch verification + live UI/API smoke (controller-level)

**Files:** none created (fixes go to the owning task's files).

- [ ] **Step 1: Full backend build + test sweep (sequential, one dotnet at a time)**
```bash
dotnet build AlgoTradeForge.slnx
dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/
dotnet test tests/AlgoTradeForge.WebApi.Tests/
dotnet test tests/AlgoTradeForge.Domain.Tests/
dotnet test tests/AlgoTradeForge.Application.Tests/
dotnet test tests/AlgoTradeForge.Infrastructure.Tests/
```
Expected: build 0/0; all green. Also build the private full solution if strategies are touched (they are not, but confirm): `dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx`.

- [ ] **Step 2: Frontend sweep** — from `frontend/`: `npm test` + `npx tsc --noEmit`. Expected: green, 0 type errors.

- [ ] **Step 3: Live API smoke with a sandbox DataRoot** (copy of `HistoryTest` MINUS existing ticks ≈ 555 MB; destroy afterward; `git restore` `appsettings.json` — `AppSettingsWriter` persists discovered dates into the working tree). Run the WebApi on `--urls http://localhost:5211` (NOT while the `:5210` Windows service runs). **Verify against real `data.binance.vision`:**
  1. **Ticks:** `POST /api/v1/loads` `{symbol: "BTCUSDT", assetType: "perpetual", feedName: "ticks", interval: "", from: "2024-03-01", to: "2024-03-31"}` → poll to complete → assert `ticks/2024-03-01.csv` … `2024-03-31.csv` exist, rows are **scaled longs** (`ts,price,qty,is_buyer_maker,agg_id`, integer price/qty), and `GET /coverage` reports 2024-03 in the ticks `covered_months`. **Confirm the aggTrades archive column order** (esp. `transact_time` index and spot's `is_best_match`) against the actual zip — adjust Task 4 indices if Binance differs.
  2. **Disk-guard:** `POST /loads` ticks with a 30-month range → **422 `tick_load_too_large`**.
  3. **Funding:** `POST /loads` `{feedName: "funding-rate", interval: "", from: "2024-01-01", to: "2024-03-01"}` → complete → `funding-rate/2024-01.csv` has `ts,rate,mark_price` with non-zero mark_price. **Verify the fundingRate archive column order** against the real zip.
  4. **Taker-volume:** `POST /loads` `{feedName: "taker-volume", interval: "15m", from: "2024-01-01", to: "2024-02-01"}` → complete → `taker-volume/2024-01_15m.csv` has `ts,buy_vol_usd,sell_vol_usd,ratio`.
  5. **End-to-end tick read:** run a tick-source aggregation over the materialized ticks (or a backtest that consumes them) → confirm `PartitionedSourceReader` parses the scaled-long rows with no `FormatException` (the payoff of Task 3).
- [ ] **Step 4: Live UI smoke via the `/validate` skill** (ports 5000/5051/3000, test DataRoot `HistoryTest`, Playwright MCP): Data tab → "Load archive data" → pick **Ticks** (no interval control), submit a small closed-month range → job card progresses → completes → coverage summary shows the tick month. Repeat for funding-rate + taker-volume. Confirm the lazy flip: with the stack running, a fresh eager cycle shows NO scheduled collection for `taker-volume`/`funding-rate`/`ticks` (now lazy), while `liquidations` (irreplaceable) still streams.
- [ ] **Step 5: Ledger + docs.** Controller starts a fresh `.superpowers/sdd/progress.md` for Phase 3 (Phase 2 ledger already archived to `progress-phase2.md`) and commits the Task 13 doc edits.

**Report results to Andrew; the smoke of Phases 1–2 paid off twice — do not skip it.**

---

## Self-Review (run against the spec, fresh eyes)

- **Spec §7.3 coverage:** AggTradesMaterializer + disk-guard (Tasks 4, 9); spot 1s — **explicitly dropped** (owner decision, recorded in Scope + Task 13); FundingRateMaterializer (Task 5); taker-volume via materialization (Tasks 6, 7). §2 monthly-completeness predicate for ticks AND funding-rate (Tasks 1, 2, 10). §1 classification flip → lazy (Task 8 + Collection-policy section). Phase-2 carryovers M6/M7 (Task 11). Vision-doc follow-up (Task 13).
- **Interval-less discriminator (review fix):** the plan's first draft keyed empty-interval handling on `== Ticks`, which would 422/throw/hide `funding-rate` (also `Interval == ""`) in the validator, coverage predicate, and coverage endpoint. Now routed through `FeedNames.UsesMonthlyCompleteness` (Task 1), consumed by Tasks 2, 5, 9, 10 — funding is handled identically to ticks.
- **Type consistency:** `IArchiveMaterializer.MaterializeMonth` identical across Tasks 4/5/6; `ArchiveMonthResult(RowsWritten, AvailableAtSource)` uniform (replace-guard returns `(0, true)`, Task 11); `IsMonthCovered`'s new `completeMonths` param threaded through all three call sites (Task 2); `ITickFeedWriter.Write(assetDir, record, decimalDigits)` updated at ALL THREE callers — `AggTradeFeedCollector` + `SpotAggTradeStreamService` (Task 3) + `TradeProjection` (Task 3b); `FeedStatus.CompleteMonths` produced in Task 1, consumed in Tasks 2/4/5/10; `MaxTickMonthsPerRequest` defined (Task 9) matches the validator.
- **CompleteMonths preservation (review fix):** `FeedStatus` is a `sealed class` with a rebuild-from-scratch `MergeStatus`, so adding `CompleteMonths` (Task 1) silently makes `MergeStatus` erase it on every per-month save. Because Tasks 4/5 run `MergeStatus → MarkCompleteMonth` per month, a multi-month backfill would keep only the last month's marker (coverage re-materializes the rest forever). Task 1 now also patches the `MergeStatus` rebuild to carry `CompleteMonths` forward, guarded by `MergeStatus_PreservesCompleteMonths`; Task 11's M6 edits the same rebuild and is cross-noted to keep the line.
- **Memory safety (review fix):** the tick materializer streams (monotonic-watermark dedup + per-UTC-day flush, no `HashSet`/sort), and `ArchiveStatusMerger.CountDataRows` is made streaming — a ~10⁸-row month is never materialized (Task 4).
- **Placeholder scan:** archive column layouts (aggTrades, fundingRate) are stated as canonical Binance formats with an explicit **verify-against-live-zip** step in Task 14 — the one unavoidable unknown (no repo fixture documents them), handled the same way Phase 1 handled its URL shapes.
- **Known accepted risks:** funding `mark_price` ≈ 8h-boundary close, with a leading 0.0 before the first close (spec-accepted; documented for auto-apply consumers, Task 5); **Tasks 3 + 3b + 4 land as one unit** — the tick-writer signature change (3) breaks `TradeProjection`'s compile until 3b, and both must precede any live tick load so archived closed months and the REST/canonicalization tails all write scaled-long; existing raw-decimal tick partitions are cleared/re-materialized (Task 3b step 5 / Task 14); long tick jobs hold the per-symbol lock for the load's duration (spec §5 trade-off, unchanged).

## Follow-ups (explicitly OUT of this phase)

- Per-asset-type `SourceInterval` in `HistoryFeedResolverFactory` + `CsvDataSource` — prerequisite for ever consuming spot `1s` (only if a concrete need arises; the spec's "consumes unchanged" was inaccurate).
- SSE for load jobs (polling is fine at month granularity); a replenishable-feed *options* endpoint to replace the hand-mirrored FE `ARCHIVE_FEEDS` constant.
- Per-feed (not per-symbol) orchestrator lock granularity — revisit if long tick jobs bite (spec §5).
- Deribit/Binance options-gamma (GEX) collectors — separate feature after the backfill phases.
