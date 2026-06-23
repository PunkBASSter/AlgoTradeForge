# LiveHost Data Plane Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reconnect bar→strategy delivery (severed in Plan 3) via an instrument-keyed data-plane dispatch, with shared live alt-bar accumulators fed once per `(instrument, bar-spec)`, plus a first-class tick path — all on one shared aggregation engine used by both the history and live hosts.

**Architecture:** Relocate the alt-bar accumulator engine from `HistoryLoader.Application/Aggregation` into `Domain/Aggregation` (one engine, two drivers). Add data-plane seams (`ITickRouter`, `IStrategyDispatch`, `IBarSourceResolver`, `IBarSource`) in `LiveHost.Application` with in-process impls in `LiveHost.Infrastructure`. The ingest pump fans each canonical `TradeTick` to two sinks — archival (lossless, unchanged) and dispatch (best-effort). Completed bars are built once per `(instrument, spec)` and fanned to every subscribed strategy; raw ticks fan to tick-subscribed strategies. Market-data delivery rides a **second** per-session bounded channel (drop-newest) drained by the existing single processing task, so fills never queue behind or get dropped by market data.

**Tech Stack:** C# 14 / .NET 10, `System.Threading.Channels` (bounded), xUnit + NSubstitute, Serilog, BenchmarkDotNet (dispatch hot-path).

## As-Built Amendments (post-implementation — read FIRST)

The task list below is the *original* plan and remains accurate for sequencing/rationale, but the as-built code differs in a few deliberate ways discovered during implementation. Where this section conflicts with a task body, **this section governs.**

**Commit structure (squashed for review):** the branch was restructured to 4 commits — `cc1dfe0` (design doc), `2ab0815` (this plan), `2ae5f5b` (engine → `Domain.Aggregation`, Tasks 1–3), `d86ec18` (data plane + §D + wiring, Tasks 4–17), plus `4dadd12` (the capability-routing amendment below). Task-level commit SHAs in the bodies are historical.

**§D tick path — SUPERSEDED by capability-driven routing (Task 4 / Task 8 / Task 10 / Task 15).** The plan's design (a defaulted `IInt64BarStrategy.OnTick` + a `LiveEventRouting.OnTick` flag gating delivery) was replaced because the flag duplicates what the type already declares. As built:
- Trade-tick entry point lives on a dedicated capability interface **`ITradeTickStrategy.OnTradeTick(in TradeTick, DataSubscription)`** (`Domain.Strategy`), NOT a defaulted method on `IInt64BarStrategy`.
- **`LiveEventRouting.OnTick` does NOT exist** — the enum is `OnBarStart | OnBarComplete | OnTrade` only.
- **Tick routing is capability-driven:** `StrategyDispatch.DispatchTick` delivers iff `session.Strategy is ITradeTickStrategy` AND the session has a `TickSubscription` for the instrument — the implemented interface IS the opt-in, so flag and method can't drift. `SessionInterest` caches the `ITradeTickStrategy?` cast and drops tick subscriptions on non-tick-capable strategies.
- **Bars remain flag-routed** (`OnBarStart`/`OnBarComplete`) — unchanged. The full bar/quote capability split (`IBarStrategy`, `IQuoteTickStrategy`, backtest-side capability routing) is deferred to **Strategy Framework v2**, along with the `OnQuoteTick` entry point (no quote-driven strategy exists yet).

**Other as-built deltas (already reflected in the design doc):** `ScaleTagAssertion` moved to Domain with `AccumulatorEntry` (Task 2); the ingest fan-out uses a `Live.Relay`-local `IRelayTradeTap` bridged by `TickRouterTradeTap` rather than an `ITickRouter` param (Task 13, avoids a layering inversion); `AltBarFeedId` moved to `Domain.Aggregation` + new `ThresholdResolver.ResolveParsed` for the threshold freeze (Task 12); the Renko resume seam crosses the now-internal boundary via public `IBarAccumulator.SeedResumeState`/`TryGetResumeState` (Task 2); `IBarSource.Start()` is awaited once-on-create in `EnsureSources` so kline sources actually subscribe (Task 15 fix); `TickAggregationBarSource.Recent` is lock-guarded for the cross-thread snapshot read (Task 16 fix).

## Global Constraints

- **One `dotnet` process at a time** — build/test/run strictly sequential, never parallel. Use `powershell.exe` (not `pwsh`).
- **Commit messages via bash heredoc + `git commit -F`** — never PowerShell `Out-File` (UTF-8 BOM). End commits with the two trailer lines used on prior LiveHost commits.
- **Per-branch commit authorization granted** for `feat/livehost-data-plane`. After every commit run `git status --porcelain` and report leftovers (Plan-3 had a namespace-orphan incident from implementers staging only their own files).
- **Verify `git log`/reflog after any commit.** Do NOT use worktree-isolated subagents that commit — commit directly on the branch.
- **Int64 money convention** — `MoneyConvert.ToLong` in Domain, `ScaleContext` at boundaries. The tick→`SourceRecord` adapter copies already-scaled `long` fields; no re-scaling.
- **Every channel bounded** (§A invariant 1). The order/execution path must never queue behind, or be dropped by, market data.
- **No `Async` suffix** on new async methods; `CancellationToken ct = default` on every I/O-bound method.
- **Comment convention** — terse, only for non-obvious algorithm/pitfall/TODO; no signature restatement.
- **One type per file**, named after the type. Exception: existing multi-type files being *relocated* keep their grouping (do not bulk-split during a move).
- **BG-service catch filter** — never `catch when (ex is not OperationCanceledException)`; use the existing `IsTrueShutdown(ex, ct)` / `catch (OperationCanceledException) when (ct.IsCancellationRequested)` pattern.
- **Resource release** — `using` over `try`/`finally` for pure releases.
- **Perf/alloc** — dispatch hot-path regressions go through the BenchmarkDotNet harness (`run-benchmarks`), not ad-hoc timing asserts.

## Verification commands (run sequentially)

```bash
dotnet build AlgoTradeForge.slnx
dotnet test tests/AlgoTradeForge.Domain.Tests/
dotnet test tests/AlgoTradeForge.HistoryLoader.Application.Tests/   # engine-relocation regression gate
dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/
dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/
dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/
```
(Adjust test-project paths if the repo nests them differently — verify with `git ls-files 'tests/**/*.csproj'` first.)

## File Structure

**Phase A — Engine relocation (Domain):**
- `src/AlgoTradeForge.Domain/Aggregation/IBarAccumulator.cs` (moved; contract + companion value types + `NoOpBarAccumulator`)
- `src/AlgoTradeForge.Domain/Aggregation/Accumulators/*.cs` (moved; `AccumulatorBase` + 8 accumulators, stay `internal`)
- `src/AlgoTradeForge.Domain/Aggregation/AccumulatorEntry.cs` (moved + split out; public factory)
- `src/AlgoTradeForge.Domain/Aggregation/ScaleTagAssertion.cs` (moved; depended on by `AccumulatorEntry`)
- `src/AlgoTradeForge.Domain/Aggregation/ThresholdResolver.cs`, `ThresholdValue.cs`, `StreamingMedianEstimator.cs` (moved)
- `src/AlgoTradeForge.Domain/Aggregation/TickToSourceRecord.cs` (new; tick→`SourceRecord` adapter)

**Phase B/C — §D + bar sources (LiveHost.Application):**
- `src/AlgoTradeForge.Domain/Live/LiveEventRouting.cs` (modify; add `OnTick`)
- `src/AlgoTradeForge.Domain/Strategy/IInt64BarStrategy.cs` (modify; add default `OnTick`)
- `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/IBarSource.cs`, `ITickFedBarSource.cs`, `IBarSourceResolver.cs`, `BarSpecKey.cs`
- `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs`
- `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/ITickRouter.cs`, `IStrategyDispatch.cs`, `LiveSessionRegistration.cs`

**Phase D/E/F — dispatch + wiring (LiveHost.Infrastructure / WebApi / Live.Relay):**
- `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/StrategyDispatch.cs`, `TickRouter.cs`, `BarSourceResolver.cs`, `KlineVenueBarSource.cs`
- `src/AlgoTradeForge.Live.Relay/IRelayTradeTap.cs` (new; `Live.Relay`-local tap to avoid dependency inversion)
- `src/AlgoTradeForge.Live.Relay/RelayIngest.cs` (modify; optional tap param)
- `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/TickRouterTradeTap.cs` (new; bridges `IRelayTradeTap` → `ITickRouter`)
- `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs` (modify; second per-session data channel, dispatch registration)
- `src/AlgoTradeForge.LiveHost.Application/Live/StartLiveSessionCommandHandler.cs` (modify; lift restriction)
- `src/AlgoTradeForge.LiveHost.WebApi/RelayPumpHostedService.cs` + `Program.cs` DI (modify; wire tap)
- `GetSessionSnapshotAsync` / `LiveSessionSnapshot` population path (modify)

---

## Phase A — Engine relocation into Domain (prerequisite)

> These are mechanical relocations across assemblies. TDD does not apply to a move; the **regression gate is the unchanged HistoryLoader aggregation suite plus a green full-solution build**. Do the move, fix references, build, run suites. Keep behavior identical — bundle no logic edits.

### Task 1: Move the accumulator contract + value types into Domain

**Files:**
- Move: `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/IBarAccumulator.cs` → `src/AlgoTradeForge.Domain/Aggregation/IBarAccumulator.cs`
- Modify: `src/AlgoTradeForge.Domain/AlgoTradeForge.Domain.csproj` (add `InternalsVisibleTo`)
- Modify: every `HistoryLoader.Application` file that used these types (add `using AlgoTradeForge.Domain.Aggregation;`)

**Interfaces:**
- Produces: namespace `AlgoTradeForge.Domain.Aggregation` containing `IBarAccumulator`, `SourceRecord`, `AggregatedBar`, `AggregationStats`, `SidecarRow`, `SidecarSchema`, `CandleExtJoinMode`, `NoOpBarAccumulator` (signatures unchanged from the original file).

- [ ] **Step 1:** Move the file to `src/AlgoTradeForge.Domain/Aggregation/IBarAccumulator.cs` and change line 1 to `namespace AlgoTradeForge.Domain.Aggregation;`. Keep the file's multi-type grouping intact (relocation exception to one-type-per-file). The companion `<see cref="AlgoTradeForge.Domain.ScaleContext"/>` reference now resolves locally.

- [ ] **Step 2:** Add to `AlgoTradeForge.Domain.csproj` `<ItemGroup>`:
```xml
<InternalsVisibleTo Include="AlgoTradeForge.Domain.Tests" />
```
(append; do not remove the existing `InternalsVisibleTo` entries).

- [ ] **Step 3:** Build. Fix every resulting `CS0246`/`CS0234` in `HistoryLoader.Application` by adding `using AlgoTradeForge.Domain.Aggregation;` (the accumulators, `AggregationPipeline`, `PartitionedSourceReader`, `AggregationJob`, `CandleExtJoiningSource`, `ScaleTagAssertion`, etc.). Do not change any logic.
```bash
dotnet build AlgoTradeForge.slnx
```
Expected: 0 errors.

- [ ] **Step 4:** Run the regression gate.
```bash
dotnet test tests/AlgoTradeForge.HistoryLoader.Application.Tests/
```
Expected: all green (same count as before the move).

- [ ] **Step 5:** Commit.
```bash
git add -A && git commit -F- <<'EOF'
refactor(aggregation): relocate IBarAccumulator contract + value types to Domain

Move IBarAccumulator, SourceRecord, AggregatedBar, AggregationStats, SidecarRow,
SidecarSchema, CandleExtJoinMode, NoOpBarAccumulator into AlgoTradeForge.Domain.Aggregation
so both the history and live hosts share one engine. Behavior unchanged.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
git status --porcelain
```
Expected: clean working tree; report any leftover.

### Task 2: Move the accumulators, factory, and scale-tag assertion into Domain

**Files:**
- Move: `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/Accumulators/*.cs` (9 files: `AccumulatorBase` + 8) → `src/AlgoTradeForge.Domain/Aggregation/Accumulators/`
- Split + move: `AccumulatorEntry` (currently in `Aggregation/ScaleTagAssertion.cs`) → `src/AlgoTradeForge.Domain/Aggregation/AccumulatorEntry.cs`
- Move: `ScaleTagAssertion` → `src/AlgoTradeForge.Domain/Aggregation/ScaleTagAssertion.cs` (it depends only on `ScaleContext`; `AccumulatorEntry` depends on it, so it must move too — this corrects the spec which said it stays)

**Interfaces:**
- Consumes: `AlgoTradeForge.Domain.Aggregation` types (Task 1).
- Produces: namespace `AlgoTradeForge.Domain.Aggregation.Accumulators` (accumulators stay `internal`); `AlgoTradeForge.Domain.Aggregation.AccumulatorEntry.Open(string typeCode, long threshold, ScaleContext sourceScale, ScaleContext accumulatorScale, DataFeedKind sourceKind = DataFeedKind.Tick) → IBarAccumulator`; `AlgoTradeForge.Domain.Aggregation.ScaleTagAssertion.Assert(ScaleContext, ScaleContext)`.

- [ ] **Step 1:** Move all 9 accumulator files; change each namespace to `AlgoTradeForge.Domain.Aggregation.Accumulators`. Keep the `internal` modifier on every accumulator class. `EqIV/EqID/EqIT` keep `using AlgoTradeForge.Domain;` for `ScaleContext` (now same assembly).

- [ ] **Step 2:** Create `src/AlgoTradeForge.Domain/Aggregation/AccumulatorEntry.cs` with namespace `AlgoTradeForge.Domain.Aggregation`, containing the `AccumulatorEntry` class verbatim from the old `ScaleTagAssertion.cs` (the `Open(...)` switch). The accumulator references become `new Accumulators.EqVAccumulator(...)` etc. (same as today; `Accumulators` is now a sub-namespace of the same assembly). Add `using AlgoTradeForge.Domain.History;` for `DataFeedKind`.

- [ ] **Step 3:** Move `ScaleTagAssertion` into `src/AlgoTradeForge.Domain/Aggregation/ScaleTagAssertion.cs` (namespace `AlgoTradeForge.Domain.Aggregation`). Delete the old `HistoryLoader.Application/Aggregation/ScaleTagAssertion.cs`.

- [ ] **Step 4:** Build; fix references in `HistoryLoader.Application` (`AggregationPipeline` calls `AccumulatorEntry.Open` and pattern-matches `RenkoAccumulator` — but `RenkoAccumulator` is now `internal` to **Domain**, unreachable from HistoryLoader!).
  **Resolution:** the `accumulator is RenkoAccumulator renko` checks in `AggregationPipeline.cs` (lines ~90 and ~374) cross an assembly boundary into Domain internals. Replace the type-pattern with the existing public `IBarAccumulator.TryDrainQueued(out AggregatedBar)` contract (Renko already exposes its multi-emit via that public method — see `IBarAccumulator.cs`). Drain queued bars in a `while (acc.TryDrainQueued(out var extra))` loop instead of casting to `RenkoAccumulator`. This keeps accumulators `internal` to Domain with zero behavior change (the drain method is what the cast was used to reach).
```bash
dotnet build AlgoTradeForge.slnx
```
Expected: 0 errors.

- [ ] **Step 5:** Regression gate.
```bash
dotnet test tests/AlgoTradeForge.HistoryLoader.Application.Tests/
```
Expected: all green (unchanged count). If a Renko test changes behavior, the drain-loop refactor was not equivalent — fix it, do not accept a diff.

- [ ] **Step 6:** Commit (`refactor(aggregation): relocate accumulators + AccumulatorEntry + ScaleTagAssertion to Domain`). `git status --porcelain`; report leftovers.

### Task 3: Move threshold resolution + streaming median into Domain

**Files:**
- Move: `Aggregation/ThresholdResolver.cs`, `Aggregation/StreamingMedianEstimator.cs` → `src/AlgoTradeForge.Domain/Aggregation/`
- Move: `ThresholdValue` (from `src/AlgoTradeForge.HistoryLoader.Domain/...`) → `src/AlgoTradeForge.Domain/Aggregation/ThresholdValue.cs`
- Move (tests): pure-engine unit tests → `tests/AlgoTradeForge.Domain.Tests/Aggregation/`

**Interfaces:**
- Produces: `AlgoTradeForge.Domain.Aggregation.ThresholdResolver.Resolve(string thresholdUnit, string inputMode, decimal? thresholdValue, string? convenienceInput, ScaleContext scale) → ThresholdResolver.Resolved`; `StreamingMedianEstimator`; `ThresholdValue`.

- [ ] **Step 1:** Locate `ThresholdValue` (`git grep -n "class ThresholdValue\|record ThresholdValue" src/AlgoTradeForge.HistoryLoader.Domain`). Move it to `src/AlgoTradeForge.Domain/Aggregation/ThresholdValue.cs`, namespace `AlgoTradeForge.Domain.Aggregation`. Verify it has no other `HistoryLoader.Domain`-only dependency (`git grep` its members); if it does, stop and report — the dependency must also be Domain-safe.

- [ ] **Step 2:** Move `ThresholdResolver.cs` and `StreamingMedianEstimator.cs` to Domain (namespace `AlgoTradeForge.Domain.Aggregation`). `ThresholdResolver`'s `using AlgoTradeForge.HistoryLoader.Domain;` (for `ThresholdValue`) becomes the local namespace; drop it.

- [ ] **Step 3:** Build; fix `HistoryLoader` references (add `using AlgoTradeForge.Domain.Aggregation;` where `ThresholdResolver`/`ThresholdValue`/`StreamingMedianEstimator` were used).
```bash
dotnet build AlgoTradeForge.slnx
```
Expected: 0 errors.

- [ ] **Step 4:** Move the pure-engine unit-test files (accumulator math, threshold-resolver, streaming-median tests — identify via `git grep -l "ThresholdResolver\|StreamingMedianEstimator\|EqVAccumulator" tests/AlgoTradeForge.HistoryLoader.Application.Tests`) into `tests/AlgoTradeForge.Domain.Tests/Aggregation/`, renamespacing the test classes. Leave pipeline/driver/storage tests (those using `AggregationPipeline`, `PartitionedSourceReader`, `IFileStorage`) in HistoryLoader. Accumulator internals are reachable because Task 1 added `InternalsVisibleTo("AlgoTradeForge.Domain.Tests")`.

- [ ] **Step 5:** Run both suites.
```bash
dotnet test tests/AlgoTradeForge.Domain.Tests/
dotnet test tests/AlgoTradeForge.HistoryLoader.Application.Tests/
```
Expected: combined green count equals the pre-move HistoryLoader count.

- [ ] **Step 6:** Commit (`refactor(aggregation): relocate ThresholdResolver/ThresholdValue/StreamingMedianEstimator + engine tests to Domain`). `git status --porcelain`.

> **Reviewer note (opus):** Tasks 1–3 are the concurrency-irrelevant but structurally-critical extraction. Verify zero behavior change: the Renko drain-loop refactor (Task 2 Step 4) is the only logic-adjacent edit and must be exactly equivalent to the old type-cast path.

---

## Phase B — §D strategy event model (foundational, no data-plane deps)

### Task 4: Add the `OnTick` routing flag and strategy entry point

**Files:**
- Modify: `src/AlgoTradeForge.Domain/Live/LiveEventRouting.cs`
- Modify: `src/AlgoTradeForge.Domain/Strategy/IInt64BarStrategy.cs`
- Test: `tests/AlgoTradeForge.Domain.Tests/Strategy/Int64BarStrategyTickRoutingTests.cs`

**Interfaces:**
- Produces: `LiveEventRouting.OnTick = 8`; `IInt64BarStrategy.OnTick(in TradeTick tick, DataSubscription subscription)` (default no-op).

- [ ] **Step 1: Write the failing test**
```csharp
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Live;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy;

public class Int64BarStrategyTickRoutingTests
{
    private sealed class BarOnlyStrategy : IInt64BarStrategy
    {
        public string Version => "1";
        public IList<DataSubscription> DataSubscriptions { get; } = new List<DataSubscription>();
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public void OnBarComplete(Int64Bar bar, DataSubscription subscription) { }
    }

    private sealed class TickStrategy : IInt64BarStrategy
    {
        public string Version => "1";
        public IList<DataSubscription> DataSubscriptions { get; } = new List<DataSubscription>();
        public TradeTick? Last;
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public void OnBarComplete(Int64Bar bar, DataSubscription subscription) { }
        public void OnTick(in TradeTick tick, DataSubscription subscription) => Last = tick;
    }

    [Fact]
    public void OnTick_flag_has_distinct_bit_and_is_in_All()
    {
        Assert.Equal(8, (int)LiveEventRouting.OnTick);
        Assert.True(LiveEventRouting.All.HasFlag(LiveEventRouting.OnTick));
    }

    [Fact]
    public void Default_OnTick_is_noop_and_does_not_force_bar_only_strategies_to_implement_it()
    {
        IInt64BarStrategy s = new BarOnlyStrategy();
        var tick = new TradeTick(1, 100, 5, 7, AggressorSide.Buy);
        s.OnTick(in tick, new DataSubscription(null!, default)); // compiles + no throw via default impl
    }

    [Fact]
    public void Overridden_OnTick_receives_the_tick()
    {
        var s = new TickStrategy();
        var tick = new TradeTick(1, 100, 5, 7, AggressorSide.Buy);
        ((IInt64BarStrategy)s).OnTick(in tick, new DataSubscription(null!, default));
        Assert.Equal(7, s.Last!.Value.Sequence);
    }
}
```

- [ ] **Step 2: Run — verify it fails** (`OnTick` undefined / `OnTick` flag missing).
```bash
dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter Int64BarStrategyTickRoutingTests
```
Expected: FAIL (compile error: `LiveEventRouting` has no `OnTick`; `IInt64BarStrategy` has no `OnTick`).

- [ ] **Step 3: Implement.** In `LiveEventRouting.cs`:
```csharp
[Flags]
public enum LiveEventRouting
{
    None = 0,
    OnBarStart = 1,
    OnBarComplete = 2,
    OnTrade = 4,
    OnTick = 8,
    All = OnBarStart | OnBarComplete | OnTrade | OnTick,
}
```
In `IInt64BarStrategy.cs` add the default method (mirrors the existing `OnBarStart` default) and the `using`:
```csharp
using AlgoTradeForge.Domain.History;
// ...
public interface IInt64BarStrategy : IStrategy
{
    void OnBarStart(Int64Bar bar, DataSubscription subscription) { }
    void OnBarComplete(Int64Bar bar, DataSubscription subscription);
    void OnTick(in TradeTick tick, DataSubscription subscription) { }
}
```

- [ ] **Step 4: Run — verify pass.**
```bash
dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter Int64BarStrategyTickRoutingTests
```
Expected: PASS.

- [ ] **Step 5: Commit** (`feat(strategy): add OnTick entry point + LiveEventRouting.OnTick flag`). `git status --porcelain`.

---

## Phase C — Tick adapter + tick-aggregation bar source + golden test

### Task 5: Tick→SourceRecord adapter

**Files:**
- Create: `src/AlgoTradeForge.Domain/Aggregation/TickToSourceRecord.cs`
- Test: `tests/AlgoTradeForge.Domain.Tests/Aggregation/TickToSourceRecordTests.cs`

**Interfaces:**
- Consumes: `TradeTick` (Domain.History), `SourceRecord` (Domain.Aggregation).
- Produces: `static SourceRecord TickToSourceRecord.From(in TradeTick tick)`.

- [ ] **Step 1: Write the failing test**
```csharp
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation;

public class TickToSourceRecordTests
{
    [Fact]
    public void Buy_tick_sets_buy_volume_and_count_only()
    {
        var r = TickToSourceRecord.From(new TradeTick(123, 100, 5, 9, AggressorSide.Buy));
        Assert.Equal(123, r.TsMs);
        Assert.Equal(100, r.Open);
        Assert.Equal(100, r.High);
        Assert.Equal(100, r.Low);
        Assert.Equal(100, r.Close);
        Assert.Equal(5, r.Volume);
        Assert.Equal(5, r.BuyVolumeLong);
        Assert.Equal(1, r.BuyTradeCountLong);
        Assert.Equal(0, r.SellVolumeLong);
        Assert.Equal(0, r.SellTradeCountLong);
    }

    [Fact]
    public void Sell_tick_sets_sell_volume_and_count_only()
    {
        var r = TickToSourceRecord.From(new TradeTick(1, 200, 7, 1, AggressorSide.Sell));
        Assert.Equal(7, r.SellVolumeLong);
        Assert.Equal(1, r.SellTradeCountLong);
        Assert.Equal(0, r.BuyVolumeLong);
        Assert.Equal(0, r.BuyTradeCountLong);
    }

    [Fact]
    public void Unknown_aggressor_leaves_directional_fields_zero()
    {
        var r = TickToSourceRecord.From(new TradeTick(1, 200, 7, 1, AggressorSide.Unknown));
        Assert.Equal(0, r.BuyVolumeLong);
        Assert.Equal(0, r.SellVolumeLong);
    }
}
```

- [ ] **Step 2: Run — verify fail** (type undefined).

- [ ] **Step 3: Implement.**
```csharp
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Domain.Aggregation;

public static class TickToSourceRecord
{
    public static SourceRecord From(in TradeTick tick)
    {
        var buy = tick.Aggressor == AggressorSide.Buy;
        var sell = tick.Aggressor == AggressorSide.Sell;
        return new SourceRecord(
            TsMs: tick.TimestampMs,
            Open: tick.Price, High: tick.Price, Low: tick.Price, Close: tick.Price,
            Volume: tick.Quantity,
            BuyVolumeLong: buy ? tick.Quantity : 0L,
            SellVolumeLong: sell ? tick.Quantity : 0L,
            BuyTradeCountLong: buy ? 1L : 0L,
            SellTradeCountLong: sell ? 1L : 0L);
    }
}
```

- [ ] **Step 4: Run — verify pass.** **Step 5: Commit** (`feat(aggregation): add tick->SourceRecord adapter`). `git status --porcelain`.

### Task 6: `IBarSource` + `TickAggregationBarSource`

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/IBarSource.cs`, `ITickFedBarSource.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/TickAggregationBarSourceTests.cs`

**Interfaces:**
- Consumes: `AccumulatorEntry.Open` + `TickToSourceRecord.From` (Domain), `Int64Bar`/`TradeTick` (Domain.History), `ScaleContext` (Domain).
- Produces:
  - `interface IBarSource { IReadOnlyList<Int64Bar> Recent { get; } }`
  - `interface ITickFedBarSource : IBarSource { void Feed(in TradeTick tick); }`
  - `sealed class TickAggregationBarSource(string typeCode, long frozenThreshold, ScaleContext scale, Action<Int64Bar> onBar, int recentCapacity = 256) : ITickFedBarSource`

- [ ] **Step 1: Write the failing test** — feed ticks, assert the source emits exactly the bars the accumulator would, via the `onBar` callback, and `Recent` retains them.
```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.DataPlane;

public class TickAggregationBarSourceTests
{
    private static ScaleContext Scale() => new(tickSize: 0.01m); // verify ScaleContext(decimal) ctor signature

    [Fact]
    public void EqV_source_emits_a_bar_each_time_volume_threshold_is_crossed()
    {
        var emitted = new List<Int64Bar>();
        var src = new TickAggregationBarSource("EqV", frozenThreshold: 10, Scale(), emitted.Add);
        // three ticks of qty 5 => 15 total, threshold 10 => one closed bar after the second tick
        src.Feed(new TradeTick(1, 100, 5, 1, AggressorSide.Buy));
        src.Feed(new TradeTick(2, 101, 5, 2, AggressorSide.Buy));
        Assert.Single(emitted);
        Assert.Equal(100, emitted[0].Open);
        Assert.Equal(101, emitted[0].Close);
        Assert.Contains(emitted[0], src.Recent);
    }
}
```
> The implementer must first confirm the real `ScaleContext` construction (`new ScaleContext(decimal tickSize)` or `new ScaleContext(asset)`) and the EqV threshold semantics by reading `EqVAccumulator`; adjust the threshold/expected-bar arithmetic to match. The test asserts *the source delegates to the accumulator faithfully*, not a re-derivation of accumulator math.

- [ ] **Step 2: Run — verify fail.**

- [ ] **Step 3: Implement.**
```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public sealed class TickAggregationBarSource : ITickFedBarSource
{
    private readonly IBarAccumulator _acc;
    private readonly Action<Int64Bar> _onBar;
    private readonly Queue<Int64Bar> _recent;
    private readonly int _recentCapacity;

    public TickAggregationBarSource(
        string typeCode, long frozenThreshold, ScaleContext scale, Action<Int64Bar> onBar, int recentCapacity = 256)
    {
        // source==accumulator scale: live ticks already carry the instrument's scale (frozen at session start)
        _acc = AccumulatorEntry.Open(typeCode, frozenThreshold, scale, scale, DataFeedKind.Tick);
        _onBar = onBar;
        _recentCapacity = recentCapacity;
        _recent = new Queue<Int64Bar>(recentCapacity);
    }

    public IReadOnlyList<Int64Bar> Recent => _recent.ToArray();

    public void Feed(in TradeTick tick)
    {
        var rec = TickToSourceRecord.From(in tick);
        if (_acc.TryAdvance(in rec, out var bar))
            Emit(ToInt64Bar(bar));
        while (_acc.TryDrainQueued(out var extra)) // Renko multi-emit
            Emit(ToInt64Bar(extra));
    }

    private void Emit(Int64Bar bar)
    {
        if (_recent.Count >= _recentCapacity) _recent.Dequeue();
        _recent.Enqueue(bar);
        _onBar(bar);
    }

    private static Int64Bar ToInt64Bar(in AggregatedBar b) =>
        new(b.TsMs, b.Open, b.High, b.Low, b.Close, b.Volume); // verify Int64Bar ctor field order
}
```
> Implementer: confirm the `Int64Bar` constructor signature/field order and the `ScaleContext` ctor before finalizing; adjust verbatim.

- [ ] **Step 4: Run — verify pass.** **Step 5: Commit** (`feat(livehost): tick-aggregation bar source over shared accumulator engine`). `git status --porcelain`.

### Task 7: Golden batch≡live acceptance test

**Files:**
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/BatchEqualsLiveGoldenTests.cs`
- Modify: `tests/AlgoTradeForge.LiveHost.Application.Tests/AlgoTradeForge.LiveHost.Application.Tests.csproj` (add `ProjectReference` to `HistoryLoader.Application` for `PartitionedSourceReader`, if not present; mirror the Plan-3 `LiveRoundTripTests` cross-host pattern)

**Interfaces:**
- Consumes: `TickAggregationBarSource` (Task 6), `PartitionedSourceReader` / the batch accumulator-feed path (HistoryLoader), `AccumulatorEntry` (Domain).

- [ ] **Step 1: Write the test.** Build one in-memory tick stream (a `List<TradeTick>`), pick a representative threshold per spec family. **Batch side:** map the same ticks to `SourceRecord` via the *batch* path (`PartitionedSourceReader` reading a tick partition, or — if a file-backed reader is too heavy for a unit test — feed `TickToSourceRecord.From` into a fresh `AccumulatorEntry.Open(...)` directly, which is exactly what the batch driver does after its reader). **Live side:** feed the same ticks to `TickAggregationBarSource`. Assert the two bar sequences are element-wise equal across `EqV, EqT, EqD, EqIV, EqID, EqIT, Range, Renko`.
```csharp
[Theory]
[InlineData("EqV")] [InlineData("EqT")] [InlineData("EqD")]
[InlineData("EqIV")] [InlineData("EqID")] [InlineData("EqIT")]
[InlineData("Range")] [InlineData("Renko")]
public void Live_driver_emits_identical_bars_to_batch_driver(string typeCode)
{
    var ticks = SyntheticTicks(count: 5000); // deterministic generator, mixed Buy/Sell
    long threshold = ThresholdFor(typeCode);
    var scale = new ScaleContext(0.01m);

    var batch = RunBatch(typeCode, threshold, scale, ticks);   // AccumulatorEntry.Open + TickToSourceRecord, batch contract
    var live = new List<Int64Bar>();
    var src = new TickAggregationBarSource(typeCode, threshold, scale, live.Add);
    foreach (var t in ticks) src.Feed(t);

    Assert.Equal(batch, live); // record-struct equality is element-wise
}
```
> The point of the test: both sides construct via `AccumulatorEntry.Open`, so equality holds **by construction** — the test guards against the *adapter* or the *source's drain/emit ordering* diverging from the batch feed. If you make the batch side go through `PartitionedSourceReader` over a temp CSV, that additionally guards the reader↔adapter equivalence; prefer that stronger form if the reader is unit-test-friendly.

- [ ] **Step 2: Run — fail (or pass trivially); ensure non-vacuous** (assert `batch.Count > 0`).
- [ ] **Step 3:** Make it pass (it should, by construction; if not, the bug is in the adapter or the source drain order — fix the source, not the test).
- [ ] **Step 4: Commit** (`test(livehost): golden batch==live bar equivalence across all alt-bar families`). `git status --porcelain`.

> **Reviewer note (opus):** this is the plan's acceptance test. Confirm it is non-vacuous (real bars on both sides) and that all 8 families are covered.

---

## Phase D — Data-plane seams + dispatch (concurrency-critical; opus reviews Tasks 9–11)

### Task 8: Data-plane interfaces + registration record + spec key

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/BarSpecKey.cs`, `IBarSourceResolver.cs`, `ITickRouter.cs`, `IStrategyDispatch.cs`, `LiveSessionRegistration.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/BarSpecKeyTests.cs`

**Interfaces (produced):**
```csharp
// BarSpecKey.cs — identifies a (bar kind) within an instrument. From a DataSubscription.
public readonly record struct BarSpecKey(string Value)
{
    public static BarSpecKey TimeBar(TimeFrame tf) => new($"time:{tf}");
    public static BarSpecKey AltBar(string feedId) => new($"alt:{feedId}");
    public static readonly BarSpecKey RawTick = new("tick");
}

// IBarSource produced in Task 6. Resolver maps a subscription to a source factory.
public interface IBarSourceResolver
{
    // returns null for raw-tick subscriptions (no bar source)
    IBarSource? Resolve(string instrument, DataFeedSubscription subscription, ScaleContext scale, Action<Int64Bar> onBar);
}

public interface ITickRouter
{
    void Publish(string instrument, in TradeTick tick);
}

public sealed record LiveSessionRegistration(
    Guid SessionId,
    IInt64BarStrategy Strategy,
    IReadOnlyList<DataSubscription> Subscriptions,   // resolved (Asset + TimeFrame) per existing model
    IReadOnlyList<DataFeedSubscription> RawSubscriptions, // typed kinds (TimeBar/AltBar/Tick) for routing
    LiveEventRouting Routing,
    System.Threading.Channels.ChannelWriter<Action> DataWriter); // session's market-data channel (drop-newest)

public interface IStrategyDispatch
{
    void Register(LiveSessionRegistration registration);
    void Unregister(Guid sessionId);
    void DispatchBar(string instrument, BarSpecKey spec, in Int64Bar bar, bool isStart);
    void DispatchTick(string instrument, in TradeTick tick);
}
```

- [ ] **Step 1:** Write a small test for `BarSpecKey` factory equality (`TimeBar(1m) == TimeBar(1m)`, `!= AltBar("EqV_...")`).
- [ ] **Step 2:** Run — fail. **Step 3:** Create the five files above (interfaces + records only; no impls yet). **Step 4:** Build + the `BarSpecKey` test pass. **Step 5:** Commit (`feat(livehost): data-plane interfaces (ITickRouter/IStrategyDispatch/IBarSourceResolver)`). `git status --porcelain`.

### Task 9: Second per-session market-data channel (drop-newest) drained by the single processing task

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs` (the `LiveSessionEntry` record ~lines 55-69, `AddSessionAsync` processing loop ~lines 192-219, drain ordering in `RemoveSessionAsync`/`StopAsync`)
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/SessionMarketDataChannelTests.cs`

**Interfaces:**
- Consumes: `IStrategyDispatch` (Task 8).
- Produces: `LiveSessionEntry.MarketDataQueue` (bounded `Channel<Action>`, `DropNewest`, drop-counter callback); the processing task drains **exec queue with priority, then market-data queue**; `LiveSessionEntry.DroppedMarketDataCount` (long).

- [ ] **Step 1: Write the failing test** (mechanism-level, mirrors the Plan-3 honest channel-level proxy): a bounded `DropNewest` data channel saturated past capacity increments the drop counter and never blocks the writer, while an exec channel at `FullMode.Wait` is drained first. Construct the two-channel drain helper in isolation if `LiveSessionEntry` is private — expose an `internal` test seam consistent with Plan-3 practice.
```csharp
[Fact]
public void Market_data_channel_drops_newest_and_counts_without_blocking_exec()
{
    long dropped = 0;
    var data = Channel.CreateBounded<Action>(
        new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropNewest, SingleReader = true },
        itemDropped: _ => Interlocked.Increment(ref dropped));
    for (int i = 0; i < 10; i++) Assert.True(data.Writer.TryWrite(() => { })); // never false with DropNewest
    Assert.True(Interlocked.Read(ref dropped) >= 8);
}
```

- [ ] **Step 2: Run — fail/establish** (the production wiring asserted in later steps doesn't exist yet).

- [ ] **Step 3: Implement.** Extend `LiveSessionEntry`:
```csharp
public long DroppedMarketDataCount;
public Channel<Action> MarketDataQueue { get; } = Channel.CreateBounded<Action>(
    new BoundedChannelOptions(MarketDataCapacity)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropNewest },
    itemDropped: _ => { /* counter incremented via closure set in ctor; see note */ });
```
> Because `itemDropped` cannot reference an instance field set later, capture the counter through a small holder or increment a `static`-free closure: simplest is to make `MarketDataQueue` lazily created in the entry ctor body where `this` is available, or store the count in a boxed `StrongBox<long>` the callback closes over. Implementer picks the cleanest; the counter MUST be readable as `DroppedMarketDataCount`.

Rework the processing task to drain both queues with exec priority:
```csharp
entry.ProcessingTask = Task.Run(async () =>
{
    var exec = entry.EventQueue.Reader;
    var data = entry.MarketDataQueue.Reader;
    try
    {
        while (true)
        {
            // exec first (fills/orders never starve behind market data)
            while (exec.TryRead(out var a)) Run(a, entry);
            if (data.TryRead(out var d)) { Run(d, entry); continue; }

            var execWait = exec.WaitToReadAsync(_cts!.Token).AsTask();
            var dataWait = data.WaitToReadAsync(_cts!.Token).AsTask();
            var ready = await Task.WhenAny(execWait, dataWait).ConfigureAwait(false);
            if (ready == execWait && !await execWait.ConfigureAwait(false)
                && ready == dataWait && !await dataWait.ConfigureAwait(false))
                break; // both completed
            // loop re-checks both readers
        }
    }
    catch (OperationCanceledException) { }
});
// Run = try { a(); } catch (Exception ex) { _logger.LogError(ex, "...session {Id} callback", entry.SessionId); }
```
> Implementer: the `WhenAny` two-channel select is the concurrency-critical core — get the completion/cancellation termination exactly right (both writers completed ⇒ exit; CTS ⇒ `OperationCanceledException`). A simpler correct alternative is acceptable if it preserves: (a) single-threaded callback execution, (b) exec priority, (c) no busy-spin. Keep `RemoveSessionAsync`/`StopAsync` drain-before-cancel ordering, and also `MarketDataQueue.Writer.TryComplete()` alongside the existing `EventQueue.Writer.TryComplete()`.

- [ ] **Step 4: Run** — Infrastructure suite green (including drain/stop tests).
```bash
dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/
```
- [ ] **Step 5: Commit** (`feat(livehost): second per-session market-data channel (drop-newest) drained with exec priority`). `git status --porcelain`.

> **Reviewer note (opus):** verify fills cannot be dropped (exec channel keeps `FullMode.Wait`, only the data channel is `DropNewest`), strategy callbacks stay single-threaded, no busy-spin, and shutdown drains both queues before CTS cancel.

### Task 10: In-process `StrategyDispatch`

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/StrategyDispatch.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/StrategyDispatchTests.cs`

**Interfaces:**
- Consumes: `LiveSessionRegistration`, `BarSpecKey`, `IInt64BarStrategy`, `LiveEventRouting`.
- Produces: `sealed class StrategyDispatch : IStrategyDispatch`. Internal index: `instrument → registrations`; per registration, the set of `(BarSpecKey)` it subscribes and whether it wants raw ticks. `DispatchBar`/`DispatchTick` enqueue gated actions onto each matching registration's `DataWriter` (drop-newest handles overflow + counts).

- [ ] **Step 1: Write failing tests:**
  - Two registrations subscribing the same `(instrument, spec)` both receive an enqueued bar action (shared-source fan-out).
  - A registration without `OnBarComplete` routing receives nothing for a bar.
  - `DispatchTick` enqueues only to registrations with a `TickSubscription` for that instrument **and** `OnTick` routing.
  - `Unregister` stops delivery.
  Use a real bounded `Channel<Action>` per fake registration; assert the actions, when run, invoke the strategy with the right bar/tick + matching `DataSubscription`.
```csharp
[Fact]
public void Bar_fans_out_to_all_sessions_subscribed_to_that_instrument_and_spec()
{
    var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
    var (regA, chA, stratA) = FakeReg("BTCUSDT", BarSpecKey.TimeBar(TimeFrame.OneMinute), LiveEventRouting.OnBarComplete);
    var (regB, chB, stratB) = FakeReg("BTCUSDT", BarSpecKey.TimeBar(TimeFrame.OneMinute), LiveEventRouting.OnBarComplete);
    dispatch.Register(regA); dispatch.Register(regB);

    var bar = new Int64Bar(1, 100, 110, 90, 105, 50);
    dispatch.DispatchBar("BTCUSDT", BarSpecKey.TimeBar(TimeFrame.OneMinute), in bar, isStart: false);

    Assert.True(chA.Reader.TryRead(out var aAction)); aAction();
    Assert.True(chB.Reader.TryRead(out var bAction)); bAction();
    Assert.Equal(105, stratA.LastBar!.Value.Close);
    Assert.Equal(105, stratB.LastBar!.Value.Close);
}
```

- [ ] **Step 2: Run — fail.** **Step 3: Implement** `StrategyDispatch` (thread-safe registry via `ConcurrentDictionary`; `DispatchBar`/`DispatchTick` allocate a closure capturing the bar/tick by value — note `in` params can't be captured, copy to a local first; gate by routing flag; `DataWriter.TryWrite(action)`). **Step 4: Run — pass.** **Step 5: Commit** (`feat(livehost): in-process StrategyDispatch fan-out`). `git status --porcelain`.

> **Reviewer note (opus):** verify routing-flag gating, `(instrument, spec)` matching, the `in`→local copy before closure capture (a captured `in` reference would be a correctness bug), and registry thread-safety under concurrent register/unregister vs dispatch.

### Task 11: In-process `TickRouter` (owns bar sources, feeds accumulators, fans to dispatch)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/TickRouter.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/TickRouterTests.cs`

**Interfaces:**
- Consumes: `IStrategyDispatch`, `IBarSourceResolver`, `ITickFedBarSource`, `TickToSourceRecord`.
- Produces: `sealed class TickRouter : ITickRouter`. Holds `Dictionary<(string instrument, BarSpecKey spec), IBarSource>` and `(string instrument) → ITickFedBarSource[]`. `Publish(instrument, tick)`: (1) feed each tick-fed source for that instrument (their `onBar` callback → `dispatch.DispatchBar(instrument, spec, bar, false)`); (2) `dispatch.DispatchTick(instrument, tick)` for raw-tick subscribers. Source creation/teardown driven by session register/unregister (a `EnsureSources(registration, scale)` / `RemoveSources(sessionId)` API the connector calls, or driven through `IStrategyDispatch` events — pick one and wire in Task 15).

- [ ] **Step 1: Write failing test:** register a `(BTCUSDT, EqV)` tick-aggregation source; `Publish` enough ticks to close a bar; assert the dispatch received a `DispatchBar` with the expected close, and `DispatchTick` was called per tick. Use a fake `IStrategyDispatch` recording calls and the real `BarSourceResolver`/`TickAggregationBarSource`.
- [ ] **Step 2: Run — fail.** **Step 3: Implement** `TickRouter` (synchronous, allocation-free `Publish` except the closures created downstream in dispatch; the source's `onBar` is set to `bar => dispatch.DispatchBar(instrument, spec, bar, isStart:false)`). **Step 4: Run — pass.** **Step 5: Commit** (`feat(livehost): TickRouter feeds shared bar sources and fans to dispatch`). `git status --porcelain`.

> **Reviewer note (opus):** `Publish` is the hot path — verify no per-tick allocation in the router itself (closures live in dispatch only when enqueuing), one source per `(instrument, spec)` (shared), and that kline (non-tick-fed) sources are *not* fed ticks here.

### Task 12: `BarSourceResolver`

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/BarSourceResolver.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/BarSourceResolverTests.cs`

**Interfaces:**
- Produces: `sealed class BarSourceResolver(...) : IBarSourceResolver`. `Resolve`:
  - `TimeBarSubscription` → `KlineVenueBarSource` (Task 14).
  - `AltBarSubscription` → `TickAggregationBarSource` (typeCode + frozen threshold parsed from `FeedId` via `ThresholdResolver`).
  - `TickSubscription` → `null` (raw tick path).

- [ ] **Step 1:** Failing test: `AltBarSubscription("...","...",role,"EqV_...")` resolves to an `ITickFedBarSource`; `TickSubscription` resolves to `null`; `TimeBarSubscription` resolves to a non-tick-fed `IBarSource`. (Stub `KlineVenueBarSource` creation if Task 14 not yet merged — order Task 14 before 12 in execution, or inject a factory.)
- [ ] **Step 2–5:** implement, test, commit (`feat(livehost): bar-source resolver (time->kline, alt->tick-aggregation, tick->raw)`). `git status --porcelain`.

> **Execution ordering:** do **Task 14 (KlineVenueBarSource) before Task 12** so the resolver wires a real time-bar source. The plan lists 12 here for grouping; the implementer/orchestrator should sequence 14 → 12.

---

## Phase E — Fan-out wiring (ingest → {archival, dispatch})

### Task 13: `Live.Relay` tick tap + `RelayIngest.Pump` fan-out + bridge

**Files:**
- Create: `src/AlgoTradeForge.Live.Relay/IRelayTradeTap.cs`
- Modify: `src/AlgoTradeForge.Live.Relay/RelayIngest.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/TickRouterTradeTap.cs`
- Test: `tests/AlgoTradeForge.Live.Relay.Tests/RelayIngestTapTests.cs`

**Interfaces:**
- Produces: `interface IRelayTradeTap { void OnTrade(string instrument, in TradeTick tick); }` (in `Live.Relay`, so the lib needs no reference to `LiveHost.Application`). `RelayIngest.Pump(..., IRelayTradeTap? tap = null)` calls `tap?.OnTrade(t.Instrument, t.Tick)` alongside the archival `WriteTrade`. Bridge `TickRouterTradeTap(ITickRouter router) : IRelayTradeTap` forwards to `router.Publish`.

- [ ] **Step 1: Failing test** in `Live.Relay.Tests`: a fake `IRelayTradeTap` receives every trade the pump archives (drive the existing `FakeVenueConnector` from Plan-3 relay tests; assert tap call count == trade count and order preserved).

- [ ] **Step 2: Run — fail.** **Step 3: Implement.** `IRelayTradeTap.cs`:
```csharp
namespace AlgoTradeForge.Live.Relay;
public interface IRelayTradeTap { void OnTrade(string instrument, in TradeTick tick); }
```
Modify `RelayIngest.Pump` signature + loop:
```csharp
public static async Task Pump(
    IVenueConnector connector, RelayWriter writer, IReadOnlyList<string> instruments,
    IRelayTradeTap? tap = null, CancellationToken ct = default)
{
    // ...existing register + Start...
    await foreach (var ev in connector.Stream(instruments, ct).ConfigureAwait(false))
    {
        switch (ev)
        {
            case TradeEvent t:
                await writer.WriteTrade(ids[t.Instrument], t.Tick, ct).ConfigureAwait(false); // archival (lossless)
                tap?.OnTrade(t.Instrument, t.Tick);                                            // dispatch (best-effort)
                break;
            case QuoteEvent q:
                await writer.WriteQuote(ids[q.Instrument], q.Quote, ct).ConfigureAwait(false);
                break;
        }
    }
}
```
> `tap` is a parameter, not a field — `RelayIngest` stays a stateless static (per the Plan-3 design note). The `TradeTick` is the same already-scaled value archived, so dispatch and archive see identical data. Update the existing `RelayPumpHostedService.RunPumpOnce` call site to pass `tap` (default `null` keeps current behavior until Task 15 injects it).

- [ ] **Step 4:** Create `TickRouterTradeTap` in `LiveHost.Infrastructure`. Build + `Live.Relay.Tests` green.
```bash
dotnet test tests/AlgoTradeForge.Live.Relay.Tests/
```
- [ ] **Step 5: Commit** (`feat(relay): optional trade tap fans ingest to dispatch alongside archival`). `git status --porcelain`.

---

## Phase F — Kline source, restriction lift, snapshot population

### Task 14: `KlineVenueBarSource` (reuse the dead kline-WS surface)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/KlineVenueBarSource.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/KlineVenueBarSourceTests.cs`

**Interfaces:**
- Consumes: the dead-in-prod kline surface — `BinanceWebSocketManager.SubscribeKline`, `BinanceKlineMessage`, `BinanceApiClient.GetKlinesAsync` (warmup). Confirm exact signatures via `git grep`.
- Produces: `sealed class KlineVenueBarSource : IBarSource, IAsyncDisposable` — subscribes the kline WS for `(instrument, timeframe)`, maps each **closed** `BinanceKlineMessage` → `Int64Bar` (via `ScaleContext.FromMarketPrice`), invokes `onBar`, maintains `Recent`. Not `ITickFedBarSource` (venue-published; not tick-driven).

- [ ] **Step 1: Failing test** — drive a fake kline message through the mapping (extract the `BinanceKlineMessage → Int64Bar` mapping into a pure `static` method `MapClosedKline(in BinanceKlineMessage, ScaleContext)` and unit-test it without a live WS). Re-home here the `// Volume: not monetary, rounding is correct` rationale at the volume-mapping line.
- [ ] **Step 2: Run — fail.** **Step 3: Implement** the mapping + WS subscription wrapper. Only **closed** klines emit (`k.IsClosed`/`x` flag — confirm field). **Step 4: Run — pass.** **Step 5: Commit** (`feat(livehost): kline venue bar source restores live time bars via dispatch`). `git status --porcelain`.

> **Reviewer note:** confirm only closed klines emit (no double-counting the forming bar), volume rounding rationale present, `IAsyncDisposable` unsubscribes the WS.

### Task 15: Lift the subscription restriction + wire session registration into the data plane

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Application/Live/StartLiveSessionCommandHandler.cs` (lines 24-39 restriction)
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs` `AddSessionAsync`/`RemoveSessionAsync` (register/unregister with `IStrategyDispatch` + ensure/remove sources via `ITickRouter`; freeze thresholds at session start)
- Modify: DI in `src/AlgoTradeForge.LiveHost.WebApi/Program.cs` (register `ITickRouter`/`IStrategyDispatch`/`IBarSourceResolver` singletons; inject `TickRouterTradeTap` into the relay pump)
- Modify: `RelayPumpHostedService` to pass the tap
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/StartLiveSessionSubscriptionKindsTests.cs`

**Interfaces:**
- Consumes: everything above.

- [ ] **Step 1: Failing tests:** `StartLiveSessionCommandHandler` now **accepts** `AltBarSubscription` and `TickSubscription` (no `NotSupportedException`), resolving each to a `DataSubscription` (Asset + appropriate `TimeFrame`/feed). Assert a session with a `TickSubscription` is admitted. (Keep an asset-not-found test.)
- [ ] **Step 2: Run — fail** (currently throws on non-TimeBar).
- [ ] **Step 3: Implement.** Replace the `if (sub is not TimeBarSubscription tb) throw ...` block with a per-kind resolution:
```csharp
DataSubscription resolved = sub switch
{
    TimeBarSubscription tb => new DataSubscription(asset, tb.TimeFrame),
    AltBarSubscription ab  => new DataSubscription(asset, TimeFrame.Unspecified, feedKey: ab.FeedId), // confirm DataSubscription shape for alt
    TickSubscription      => new DataSubscription(asset, TimeFrame.Unspecified, feedKey: "tick"),
    _ => throw new NotSupportedException($"Unsupported live subscription kind: {sub.GetType().Name}"),
};
resolvedSubscriptions.Add(resolved);
```
> Confirm `DataSubscription`'s real shape (`record DataSubscription(Asset, TimeFrame, string FeedKey = "ohlcv", bool IsExportable = false)`) and how alt/tick are represented downstream; keep the typed `DataFeedSubscription` list too (the connector needs the kind to pick the bar source). Thread the **raw typed subscriptions** into `LiveSessionConfig`/`SessionDetails` so `AddSessionAsync` can build `LiveSessionRegistration`.
In `AddSessionAsync`: after creating `entry`, **freeze thresholds** (call `ThresholdResolver.Resolve` once per alt-bar subscription), build sources via `IBarSourceResolver`, `tickRouter.EnsureSources(...)`, and `dispatch.Register(new LiveSessionRegistration(... entry.MarketDataQueue.Writer ...))`. In `RemoveSessionAsync`/`StopAsync`: `dispatch.Unregister(sessionId)` + `tickRouter.RemoveSources(sessionId)` before draining.
- [ ] **Step 4: Run** — Application + Infrastructure + WebApi suites green.
- [ ] **Step 5: Commit** (`feat(livehost): accept tick/alt-bar live subscriptions and wire sessions into the data plane`). `git status --porcelain`.

> **Reviewer note (opus):** thresholds resolved **once** at session start and passed frozen to the source (live must never re-derive); register-before-first-tick / unregister-before-drain ordering; DI lifetimes (router/dispatch singletons shared across sessions and the relay pump).

### Task 16: Populate session snapshot bars from the bar sources

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs` `GetSessionSnapshotAsync` (~lines 541-566; the `bars`/`lastBars` are currently `[]`)
- Modify: `BinanceLiveSessionDataProvider.GetRecentKlinesAsync` if it should now read source `Recent`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/SessionSnapshotBarsTests.cs`

**Interfaces:**
- Consumes: `IBarSource.Recent` per `(instrument, spec)` for the session.

- [ ] **Step 1: Failing test:** after feeding ticks/klines through the router for a registered session, `GetSessionSnapshotAsync` returns non-empty `Bars` and a `LastBarsPerSubscription` entry per subscription with the latest bar.
- [ ] **Step 2–4:** implement (read `Recent` from the session's sources via the router/dispatch; populate `LiveSessionSnapshot.Bars` + `LastBarsPerSubscription`), test, ensure `GetLiveSessionDataQuery` merge still dedups by `TimestampMs`.
- [ ] **Step 5: Commit** (`feat(livehost): populate session snapshot bars/last-bars from live bar sources`). `git status --porcelain`.

> This retires the connector-testability debt: bar production is now exercised without a live connector.

---

## Phase G — End-to-end acceptance, benchmark, whole-branch review

### Task 17: End-to-end data-plane acceptance test

**Files:**
- Test: `tests/AlgoTradeForge.LiveHost.WebApi.Tests/DataPlane/DataPlaneEndToEndTests.cs` (mirror Plan-3 `LiveRoundTripTests` host wiring)

- [ ] **Step 1:** Two scenarios through the real wiring (fake venue connector emitting `TradeEvent`s):
  - **Bar path:** a strategy with an `AltBarSubscription` + `OnBarComplete` routing receives completed bars built by `TickAggregationBarSource` via dispatch (assert `OnBarComplete` called with expected closes).
  - **Tick path:** a strategy with a `TickSubscription` + `OnTick` routing receives every tick via dispatch.
  Drive ticks through `RelayIngest.Pump` with the `TickRouterTradeTap`, so the SAME stream hits archival and dispatch; assert archival `.atft` still round-trips (no regression to Plan-3 lossless guarantee) AND the strategy received its events.
- [ ] **Step 2–4:** run, fix, ensure non-vacuous. **Step 5: Commit** (`test(livehost): end-to-end data-plane acceptance (bar + tick paths, archival intact)`). `git status --porcelain`.

### Task 18: Dispatch hot-path benchmark + whole-branch review

- [ ] **Step 1:** Add/extend a BenchmarkDotNet scenario for `TickRouter.Publish` → dispatch enqueue (per-tick allocation + latency) under N instruments × M sessions, using the harness in `benchmarks/AlgoTradeForge.Benchmarks/` (see `run-benchmarks` skill). Capture a baseline on a quiet machine; record Mean + Allocated. The hot path target: near-zero per-tick allocation in `Publish` (closures only at enqueue).
- [ ] **Step 2:** Run the full verification command list (all suites green, build 0/0).
- [ ] **Step 3:** Whole-branch **opus** review covering: engine relocation behavior-equivalence, the two-channel drain correctness (fills never dropped, single-threaded callbacks), router/dispatch fan-out, threshold-freeze, archival non-regression, DI lifetimes, bounded-channel + OCE-filter conventions. Address findings; re-run suites.
- [ ] **Step 4:** Update memory note `project_service_decomposition.md` with the Plan-4-complete milestone (engine in Domain, data-plane seams, golden test, carried items to Plan 5/6). Commit (`docs/perf`: benchmark + review notes if any code changed). `git status --porcelain`.

---

## Self-Review (against the spec)

**Spec coverage:**
- §1 engine relocation → Tasks 1–3 ✓ (incl. spec correction: `ScaleTagAssertion` moves too; Renko cast → `TryDrainQueued`).
- §2 seams (`ITickRouter`/`IStrategyDispatch`/`IBarSourceResolver`) → Tasks 8, 10, 11, 12 ✓.
- §3 bar sources + adapter + threshold freeze → Tasks 5, 6, 12, 14, 15 ✓. M6 seeding correctly absent.
- §4 §D (OnTick/routing/TickSubscription/restriction lift) → Tasks 4, 15 ✓.
- §5 fan-out → Task 13 ✓ (spec correction: tap interface in `Live.Relay`, not an `ITickRouter` param, to avoid dependency inversion).
- §6 snapshot population + testability debt → Task 16 ✓.
- §7 invariants → enforced per task (bounded channels, OCE filter, Int64) ✓.
- §8 golden test + open/closed + process → Tasks 7, 17, 18 ✓.
- §9 out-of-scope (M6 seeding, multi-account, archival bars-relay-frame, collection.json) → not built; seams allow ✓.

**Placeholder scan:** representative tests carry real assertions; move-tasks gate on the unchanged HistoryLoader suite; two "confirm the real signature" notes (`ScaleContext`, `Int64Bar`, `DataSubscription`, kline fields) are deliberate — the implementer reads the type before finalizing verbatim code.

**Type consistency:** `BarSpecKey`, `LiveSessionRegistration`, `IBarSource`/`ITickFedBarSource`, `AccumulatorEntry.Open`, `TickToSourceRecord.From`, `IRelayTradeTap.OnTrade` are used with identical signatures across tasks.

**Known follow-ups (carry to ledger):** dynamic per-session venue-instrument subscription (Plan 4 supports the static/union case); richer §K drop policy; M6 partial-bar seeding + multi-account routing.
