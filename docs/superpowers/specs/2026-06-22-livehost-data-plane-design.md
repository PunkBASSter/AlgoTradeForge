# LiveHost — Data Plane (instrument-keyed dispatch + live alt-bars) — Design

**Date:** 2026-06-22
**Plan:** 4 of the LiveHost relay decomposition (Plans 1–3 complete & merged).
**Implements:** vision M3b/§B (data plane) + §D (strategy event model), per the AUTHORITATIVE internal design `docs/superpowers/specs/2026-06-20-livehost-collection-execution-design.md` (§B Data plane, §D Strategy event model, and the 2026-06-21 §B/§J alt-bar-engine addendum).
**Branch:** `feat/livehost-data-plane` (off `main`, base `62b2e82`).

## Context

Plan 3 extracted the LiveHost vertical slice (`AlgoTradeForge.LiveHost.Application/.Infrastructure/.WebApi`) and split the connector into an **ingest plane** (`BinanceVenueConnector : IVenueConnector`, `aggTrade` → canonical `TradeTick`, archived losslessly via `RelayPumpHostedService` → `RelayWriter` → `.atft` → `SegmentUploader`) and a slimmed **execution plane** (`BinanceLiveConnector` = orders/fills/3-phase reconciliation/account caching). To do so it **deliberately severed bar→strategy delivery**: it removed `OnKlineMessage`/kline-WS/bar accumulation, `GetSessionSnapshotAsync` returns empty bars, `BinanceLiveSessionDataProvider.GetRecentKlinesAsync` returns `[]`, and `StartLiveSessionCommandHandler` still rejects anything but `TimeBarSubscription`.

Plan 4 **reconnects** that seam — but via the §B dispatch model, not by restoring the old fused connector. The same canonical `TradeTick`s the ingest plane archives must *also* drive strategies: ingest → **{archival (lossless), dispatch (best-effort)}**. Completed bars are built **once** per `(instrument, bar-spec)` by a shared accumulator and fanned to every subscribed strategy; raw ticks are fanned to tick-subscribed strategies.

The alt-bar accumulator engine already exists as a **batch** layer in `HistoryLoader.Application/Aggregation/`. Its contract (`IBarAccumulator.TryAdvance(in SourceRecord, out AggregatedBar)`) is already streaming and source-agnostic, so "aggregation happens twice" must mean **two drivers feeding one engine, never two implementations**. The prerequisite for this plan is therefore to relocate that engine to a shared home both hosts can reference (LiveHost must not depend on the history host).

## Scope decisions (locked with owner, 2026-06-22)

1. **Engine home:** fold the engine into **`AlgoTradeForge.Domain/Aggregation/`** (not a new library). Domain keeps zero project references; `ScaleContext` already lives there. The one cross-host dependency (`ThresholdValue` in `HistoryLoader.Domain`) moves into Domain.
2. **Bar-source resolver:** build the per-`(instrument, bar-spec)` bar-source resolver seam. Alt-bars → tick-aggregation (shared engine). Time bars → reuse the dead-in-prod kline-WS surface as a **dispatch-only venue-published source** (preserves parity with HistoryLoader's historical kline feed). **Defer** the archival bars-relay-frame type (no consumer = YAGNI).
3. **§D tick path:** full — add `OnTick` + `LiveEventRouting.OnTick`, make `TickSubscription` first-class, lift the `TimeBarSubscription`-only restriction, with the batch≡live golden bar test as acceptance.

Sub-decisions (owner-approved):
- **`OnTick` placement:** default no-op method on `IInt64BarStrategy` (mirrors the existing `OnBarStart` default), not a separate `ITickStrategy`.
- **Dispatch back-pressure:** per-strategy bounded channel, **drop-newest + logged dropped-tick counter**, generous capacity. Archival is the lossless truth; live dispatch is best-effort and must never stall the shared pump or the order path.

## Out of scope (later plans; seams must allow them)

- **M6 partial-bar seeding** from the warmup tail + persisted CAS state → **Plan 6**. (Threshold *freeze*-at-session-start IS in this plan; partial-bar *seeding* is not.)
- **Multi-account** account-keyed `IOrderRouter` → **Plan 5**. Plan 4 is single-account/single-node (the degenerate case of the general model).
- **Archival bars-relay-frame type** (§A½ venue-published bar as a binary relay stream) — deferred until a venue that publishes bars needs archiving; `KlineVenueBarSource` feeds dispatch only.
- **collection.json roles** → Plan 6.

---

## §1 — Engine relocation into `Domain/Aggregation/` (prerequisite)

Relocate the **source-agnostic core** to `src/AlgoTradeForge.Domain/Aggregation/` (+ `Accumulators/`), namespace `AlgoTradeForge.Domain.Aggregation[.Accumulators]`.

**Moves (engine core):**
- `IBarAccumulator` and its companion types declared alongside it: `SourceRecord`, `AggregatedBar`, `AggregationStats`, `SidecarRow`, `SidecarSchema`, `CandleExtJoinMode`.
- `AccumulatorBase` + all 8 accumulators (`EqV/EqT/EqD/EqIV/EqID/EqIT/Renko/Range`).
- `ThresholdResolver`, `StreamingMedianEstimator`.
- `AccumulatorEntry` — the public factory `Open(typeCode, threshold, sourceScale, accumulatorScale, sourceKind) → IBarAccumulator`. **Split it out of `ScaleTagAssertion.cs` into its own `AccumulatorEntry.cs`** (one-type-per-file constitution).
- `ThresholdValue` — move from `HistoryLoader.Domain` into `Domain.Aggregation`. Verify no other `HistoryLoader.Domain` consumer breaks; update HistoryLoader references.

**Stays in `HistoryLoader.Application/Aggregation/` (the batch driver, behavior unchanged):**
`AggregationPipeline`, `PartitionedSourceReader`, `AggregationJob`, `MonotonicTickSource`, `CandleExtJoiningSource`, `OverwritePathWriter`, `PartitionedSinkWriter`, `EligibilityRules`, `ScaleTagAssertion` (the remaining assertion helper), `SourceTailProbe`, `AggregatedDirSweeper`, `StartupSweepService`, `ProgressEvent`, `Jobs/`, `AssetScaleContextFactory`, `PartitionFilenameParser`, `AltBarWarnings`. These keep their `IFileStorage`/`ISchemaManager` host coupling and now consume `Domain.Aggregation` types.

**Visibility:** accumulators stay `internal` to Domain; `AccumulatorEntry.Open` is the only construction path used by both drivers. Add `InternalsVisibleTo("AlgoTradeForge.Domain.Tests")` so engine unit tests can reach internals; the batch and live drivers never `new` an accumulator.

**Tests:** move pure-engine unit tests (accumulator math, threshold resolution, streaming median) to `Domain.Tests/Aggregation/`; keep pipeline/driver/storage tests in `HistoryLoader.Application.Tests`. The existing HistoryLoader aggregation suite must stay green — it is the regression guard proving the move is behavior-preserving.

**Build order:** this section ships first and independently; the full solution + HistoryLoader suite must be green before any data-plane work begins.

## §2 — Data-plane seams

New abstractions in `LiveHost.Application` (in-process impls in `LiveHost.Infrastructure`):

### `ITickRouter` — the instrument-keyed hub
```csharp
public interface ITickRouter
{
    void Publish(string instrument, in TradeTick tick);
}
```
The market-data session publishes normalized `TradeTick`s keyed by **instrument** (not account). `Publish` is synchronous, allocation-free on the hot path: it feeds the instrument's bar sources (§3) in-thread and `TryWrite`s ticks/bars to each subscribed strategy's channel. The router owns the venue-connector subscription set (instruments needed by active sessions ∪ archival config) and adjusts it on session start/stop.

### `IStrategyDispatch` — per-strategy delivery
Per strategy: one **bounded** `Channel` + a `SingleReader` processing task (the existing per-session `EventQueue`/`ProcessingTask` model, extended to carry ticks **and** shared completed bars). The reader invokes `OnTick`/`OnBarStart`/`OnBarComplete` on its single thread, gated by `LiveEventRouting`. Registration: a strategy session registers `{ instrument, spec, strategy, routing }` tuples at start and unregisters at stop.

**Back-pressure:** bounded channel, `FullMode` drop-newest, generous capacity (config `DispatchChannelCapacity`, default e.g. 4096). On a dropped tick/bar, increment a per-strategy counter and log (rate-limited). Archival is unaffected (lossless path is separate). The order path is fully independent of dispatch.

### `IBarSourceResolver` — `(instrument, bar-spec)` → source
Returns a shared `IBarSource` for each distinct `(instrument, spec)`; multiple strategies subscribing the same `(instrument, spec)` share one source (the "build once" guarantee).

## §3 — Bar sources & the live driver

```csharp
public interface IBarSource
{
    // emits completed bars to a sink; supplies a recent-bars view for snapshots
    IReadOnlyList<Int64Bar> Recent { get; }
}
```

Two implementations behind the resolver:

### `TickAggregationBarSource` (alt-bars + anything no venue publishes)
Wraps `AccumulatorEntry.Open(spec, frozenThreshold)`. Each tick is converted by the **tick→`SourceRecord` adapter** and fed via `TryAdvance`; emitted `AggregatedBar`s become `Int64Bar`s delivered to the dispatch. `Renko`'s queued-drain (`TryDrainQueued`) and imbalance sidecars are honored exactly as in the batch driver (same engine objects).

**Tick→`SourceRecord` adapter:** richer than "O=H=L=C=price, V=qty" — it also sets directional fields from `AggressorSide` so imbalance bars work live:
- `Open = High = Low = Close = tick.Price`, `Volume = tick.Quantity`.
- `Aggressor == Buy` → `BuyVolumeLong = qty`, `BuyTradeCountLong = 1`.
- `Aggressor == Sell` → `SellVolumeLong = qty`, `SellTradeCountLong = 1`.
- Ticks are already scaled-`long` from the venue connector; the adapter is a field copy, no re-scaling. Any arithmetic uses `MoneyConvert.ToLong` (Int64 money convention).

### `KlineVenueBarSource` (time bars)
Reuses the dead-in-prod surface (`BinanceWebSocketManager.SubscribeKline`, `BinanceKlineMessage`, and `BinanceApiClient.GetKlinesAsync` for warmup) as a **venue-published bar source** — restoring exactly the path Plan 3 severed and preserving parity with HistoryLoader's historical kline feed. Completed klines map to `Int64Bar` and are delivered to dispatch. This re-homes the `// Volume: not monetary, rounding is correct` rationale at the kline→bar reconstruction site (carried debt) and retires the dead-kline-surface debt.

### Threshold freeze (parity guard, M6 seam)
`ThresholdResolver.Resolve(...)` runs **once at session start**; the scaled `long` is passed to `AccumulatorEntry.Open` and frozen for the session's lifetime. Live must never re-derive thresholds (that would silently break continuation of the historical series). Partial-bar **seeding** from the warmup tail is the remaining M6 guard and is deferred to Plan 6 — but the freeze seam is here and the accumulator already accepts a construction-time threshold, so Plan 6 adds seeding without reshaping this interface.

## §4 — §D strategy event model

- `LiveEventRouting` gains `OnTick = 8`; `All = OnBarStart | OnBarComplete | OnTrade | OnTick`.
- `IInt64BarStrategy` gains a **default no-op**: `void OnTick(in TradeTick tick, DataSubscription sub) { }` (mirrors the existing defaulted `OnBarStart`). Every live strategy gets it for free; routing flags gate whether it is called. A pure-tick strategy no-ops `OnBarComplete`.
- **`TickSubscription`** (already a Domain type) becomes a first-class live subscription routed to `OnTick`.
- **Subscription → source/route mapping** in the dispatch:
  - `TimeBarSubscription` → `KlineVenueBarSource` → `OnBarStart`/`OnBarComplete`.
  - `AltBarSubscription` → `TickAggregationBarSource` → `OnBarStart`/`OnBarComplete`.
  - `TickSubscription` → raw tick path → `OnTick`.
- **Lift the restriction** in `StartLiveSessionCommandHandler` (currently `throw new NotSupportedException` on non-`TimeBarSubscription`) to accept all three subscription kinds, validating each and wiring it to the right source/route.

## §5 — Fan-out wiring (ingest → {archival, dispatch})

The ingest pump reads `IVenueConnector.Stream` **once** and fans each `IMarketEvent` to two sinks:
1. **Archival (unchanged, lossless):** `RelayWriter` → `.atft` → `SegmentUploader` → `IFileStorage`.
2. **Dispatch (new, best-effort):** `ITickRouter.Publish` → bar sources + per-strategy channels.

`RelayIngest.Pump` gains an optional `ITickRouter? router` **parameter** (not a field — it stays a stateless static, consistent with the Plan-3 design note; promote to `IRelayIngest` only if it later gains call-independent state/policy). The router manages the venue-connector subscription set so a session's instruments are streamed; in Plan 4 this is the union of archival-config instruments and active-session instruments.

## §6 — Snapshot / query population

Each bar source keeps a small per-`(instrument, spec)` recent-bars ring buffer (`IBarSource.Recent`). `GetSessionSnapshotAsync` reads from the **dispatch/bar-engine** (not the connector) to populate `LiveSessionSnapshot.Bars` and `LastBarsPerSubscription`, which flow through the existing `GetLiveSessionDataQuery` merge (historical/REST first, then session bars, dedup by `TimestampMs`).

Because bar production now lives in an injectable component rather than behind the concrete `BinanceApiClient`, the **connector-testability debt is retired**: bar delivery is unit-testable by feeding ticks to a source directly — no testnet, no connected connector required.

## §7 — Error handling & invariants

- **All new channels bounded** (§A invariant 1). Dispatch drop-newest + counter; the order path is fully independent of both dispatch and archival.
- Long-running loops use `IsTrueShutdown(ex, ct)`; never `catch when (ex is not OperationCanceledException)` (the documented BG-service crash trap).
- Bounded-channel deadlock discipline preserved (marshal-then-await uses `await WriteAsync`, not `TryWrite`, where a TCS round-trip depends on the write landing).
- `using`-over-`try`/`finally` for pure releases; `SemaphoreSlimExtensions.LockAsync` for the single static gate.
- Int64 money convention throughout the tick→`SourceRecord` adapter and any new scaling.

## §8 — Testing & acceptance

- **Golden acceptance (batch ≡ live):** a synthetic tick stream, cold start, single frozen threshold, run through **both** the batch driver (`AggregationPipeline`) and the live driver (`TickAggregationBarSource`); assert emitted bars are byte-identical across all spec families (EqV/EqT/EqD/EqIV/EqID/EqIT/Renko/Range). This is the open/closed acceptance test for the plan.
- **Severed-seam restoration:** an end-to-end test driving a `TimeBarSubscription` strategy through `KlineVenueBarSource` → dispatch → `OnBarComplete`, and a `TickSubscription`/`OnTick` strategy through the raw tick path.
- **Dispatch correctness:** shared-source fan-out (two strategies, one `(instrument, spec)`, both receive identical bars); drop-newest back-pressure increments the counter without stalling the pump; per-strategy `SingleReader` ordering.
- **Engine relocation regression:** full HistoryLoader aggregation suite green after the move.
- **Process discipline (Plans 1–3):** per-task TDD, per-task two-verdict review, **opus** for the engine extraction and the dispatch fan-out (concurrency/idempotency-critical), opus whole-branch review at the end. Allocation/latency on the dispatch hot path validated via the BenchmarkDotNet harness (`run-benchmarks`), not ad-hoc asserts.

## §9 — Risks / open points

- **Instrument-subscription lifecycle:** dynamic add/remove of venue instruments mid-stream is awkward against a fixed-list `Stream(instruments)`. Plan 4 supports the static/union case (router (re)establishes the stream for the current set); a richer dynamic-subscribe API is a contained follow-up if needed.
- **Time-bar parity depends on the kline source**, not tick re-aggregation — chosen deliberately (scope decision 2) so live time bars match the historical kline feed.
- **Dispatch drop policy** is intentionally simple for Plan 4; the richer §K backpressure/drop policy (spill, priority) remains deferred.
- **Engine relocation touches a large, well-tested subsystem** — the move must be mechanical (namespace + project), guarded by the unchanged HistoryLoader suite, with no behavior edits bundled in.
