# LiveHost Host Extraction + Ingest/Execution Seam Split — Design

**Date:** 2026-06-21
**Status:** Approved (brainstorming) — ready for implementation plan
**Scope:** Vision milestone **M3a**. Extract the live-trading code out of the shared backtest stack into a dedicated `LiveHost` host (full vertical slice), split the fused Binance connector along the §A four-planes seam, bound every channel, land the first **real** `IVenueConnector`, and wire the relay producer so live ticks archive as `.atft` — closing the live capture→archive→canonicalize round-trip end to end against the Plan-2 canonicalizer.

## Context

**Why:** The LiveHost collection+execution design (`docs/superpowers/specs/2026-06-20-livehost-collection-execution-design.md`, §A–J) defines a `LiveHost@<venue>` process as four planes (ingest / archival / dispatch / execution) sharing no hot-path locks, with two hard invariants: **every channel is bounded**, and **the order path never queues behind the archival path**. Plans 1 and 2 built the relay producer (`AlgoTradeForge.Live.Relay`) and the HistoryLoader binary-tick canonicalizer; both are merged to `main`. What does not yet exist: a host to run live in, a real venue connector emitting ticks, and the wiring that makes a live session actually archive what it sees.

Today's live code lives **inside the shared** `Application/Live` + `Infrastructure/Live/Binance`, mapped by the backtest `WebApi`. `BinanceLiveConnector` (726 lines) fuses three planes — ingest (`OnKlineMessage`: WS klines → `Int64Bar`), execution (orders, fills, 3-phase reconciliation), and account/balance caching — and uses **unbounded** channels at three sites. Critically, **none of the live connectors are in production use**, so this plan is free to restructure for the cleanest result rather than preserve backward-compatible behavior.

**Goal:** A dedicated `LiveHost` host that (a) runs the relay producer against a real Binance market-data connector so live ticks are losslessly archived to `IFileStorage` and canonicalized by HistoryLoader, and (b) holds the execution machinery (orders/fills/reconciliation) on bounded channels — with the four-planes seam made structural, ready for Plan 4 (data plane) and Plan 5 (execution plane) to wire delivery and multi-account routing without a rewrite.

**Decisions locked (this brainstorming):**
- **Full M3a** — host extraction **+** bounded channels **+** real Binance connector (push/visitor decision) **+** relay-archival round-trip, in one plan.
- **Full vertical slice** — new `LiveHost.Application` + `LiveHost.Infrastructure` + `LiveHost.WebApi`, mirroring the HistoryLoader quartet (Domain stays shared). Backtest `WebApi`/`Gateway` loses all live references.
- **Pull seam now, measure, document trigger** — the connector ships on the existing `IVenueConnector : IAsyncEnumerable<IMarketEvent>` pull seam; the push/visitor (`IVenueSink.OnTrade(in TradeTick)`) GC upgrade is deferred to a measured trigger recorded in this spec.
- **Split along the ingest/execution seam** — extract a clean `BinanceVenueConnector` (ingest plane → relay); slim `BinanceLiveConnector` to the execution plane. **Bar→strategy delivery (`IStrategyDispatch`) is intentionally severed and deferred to Plan 4.**
- **Additive tick archival + bounded execution** — the connector archives a real `aggTrade` trade-tick stream; the three unbounded channels become bounded; execution order/fill semantics are otherwise unchanged.

## Out of scope (explicitly)

- **`IStrategyDispatch` / bar→strategy delivery (Plan 4).** The seam split severs `OnBarComplete` triggering; Plan 4 rewires data delivery via the dispatch seam. In Plan 3, a live session archives ticks and runs the order machinery, but does not drive strategy bars. `GetLiveSessionData`'s candle/last-bar fields become Plan-4-populated.
- **`IOrderRouter` multi-account routing (Plan 5).** The slimmed `BinanceLiveConnector` stays single-account-scoped as today.
- **`collection.json` per-instrument roles + session/account config model (Plan 6).** Plan 3 reads collect-instrument config from `appsettings`, not the CAS `collection.json`.
- **Alt-bar accumulator shared-lib extraction (Plan 4 prerequisite)** and **live alt-bar aggregation / M6 parity guards.**
- **Spill-to-disk drop policy under sustained archival backpressure (§K open point).** Execution channels block-on-full; the archival `StreamPipeline` is already bounded (Plan 1).
- **`_session` lossless dedup redesign** — carried Plan-2 debt; revisited when the gap-detector consumer defines required fidelity.
- **`QuoteEvent` / `bookTicker` ingest** — `TradeEvent` only this plan; quotes share the same machinery and drop in later (open/closed).

## A. Project layout (full vertical slice)

```
src/AlgoTradeForge.LiveHost.Application/        ← moved from Application/Live/*
    StartLiveSessionCommand(+Handler), StopLiveSessionCommand(+Handler),
    GetLiveSessionDataQuery, ILiveSessionStore + InMemoryLiveSessionStore,
    ILiveSessionDataProvider, IExchangeOrderClient, LiveSessionSnapshot, ...
src/AlgoTradeForge.LiveHost.Infrastructure/     ← moved from Infrastructure/Live/*
    Binance/ (BinanceApiClient, BinanceWebSocketManager, BinanceLiveAccountManager,
              BinanceLiveConnector [slimmed], BinanceLiveSessionDataProvider, models),
    LiveOrderContext, OrderGroupReconciler,
    BinanceVenueConnector [NEW — ingest plane]
    refs: AlgoTradeForge.Live.Relay (producer), AlgoTradeForge.Storage (IFileStorage)
src/AlgoTradeForge.LiveHost.WebApi/             ← NEW host
    Program, Endpoints/LiveEndpoints (moved from WebApi), live DI extension,
    RelayPumpHostedService [NEW — archival wiring], appsettings.{Profile}.json
tests/AlgoTradeForge.LiveHost.Application.Tests/
tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/
```

- **Domain stays shared** — `Domain.Live` (`ILiveConnector`, `ILiveAccountManager`, `LiveSessionConfig`, `LiveEventRouting`, `LiveSessionStatus`), `TradeTick`/`IFramePayload`, Portfolio/Order/indicators. Live strategies need these.
- Backtest `WebApi`/`Gateway` removes its `Application/Live` + `Infrastructure/Live` project references and the `MapLiveEndpoints()` call. Any live DI registrations move to the `LiveHost.WebApi` DI extension.
- `AlgoTradeForge.slnx` gains 5 projects (3 src + 2 test). The full-private solution reference set is updated in lockstep.

## B. The four-planes seam split

### Ingest plane — new `BinanceVenueConnector : IVenueConnector`

- Owns a **market-data** `BinanceWebSocketManager` connection; subscribes the Binance `aggTrade` stream per instrument; normalizes each `BinanceAggTrade` DTO → canonical `TradeTick` → `TradeEvent(instrument, tick)`; yields them through `Stream(instruments, ct) : IAsyncEnumerable<IMarketEvent>` (the existing pull seam — unchanged signature).
- `AggressorSide` from `is_buyer_maker` (`buyer_maker ⇒ Sell` aggressor); `Sequence` = `aggId`; `Price`/`Quantity` scaled to long via the per-instrument scale.
- **Per-instrument scale from config** — the connector exposes `PriceScaleExp`/`QtyScaleExp` per instrument (from `BinanceLiveOptions`/instrument metadata), consumed by the relay registration. This **retires the Plan-1 hardcoded-`(2,0)` `RelayIngest` debt**: `RelayIngest`/`RegisterInstrument` take scales from the connector, not a literal.
- `SessionPolicy = Concurrent` (crypto); `Venue = "binance"` (or per-config venue id).

### Execution plane — slimmed `BinanceLiveConnector`

- **Keeps:** `ConnectAsync` (REST + user-data WS for execution reports), `AddSessionAsync` (portfolio, `LiveOrderContext`, strategy init, per-session `EventQueue` + `ProcessingTask`), `OnExecutionReport` + fill/termination handling, 3-phase `RunReconciliationLoopAsync`, account/balance/trades caching, `StopAsync` drain ordering.
- **Removes (ingest concern):** `OnKlineMessage`, kline WS subscription in `AddSessionAsync`, `MapTimeFrameToInterval`, `GetRecentKlinesAsync`, `AccumulatedBars`/`LastBarPerSub`/`BarsLock` (bar accumulation). `GetSessionSnapshotAsync` returns empty market-data fields (Plan-4-populated).
- **Severed:** `OnBarStart`/`OnBarComplete` strategy triggering. This is `IStrategyDispatch` (Plan 4). The session is constructed and order-capable; strategies receive no bars in Plan 3.

The split is clean because the `EventQueue` is a **thread-serialization mechanism, not a data feed** — reconciliation marshals module mutations onto the session thread via `TryWrite(() => ...)`, with no dependency on market data. The execution plane stands coherent without the ingest plane.

## C. Bounded channels (the opus-critical task)

Three `Channel.CreateUnbounded` sites → `Channel.CreateBounded(capacity)` with `FullMode = Wait`, capacity from options (default e.g. 1024):
1. `BinanceLiveConnector` per-session `EventQueue` (`Channel<Action>`).
2. `LiveOrderContext._orderChannel`.
3. `LiveOrderContext._cancelChannel`.

**Hazard — must not be a find-replace.** Reconciliation does `EventQueue.Writer.TryWrite(() => tcs.SetResult(...))` then `await tcs.Task` (three such round-trips: snapshot, repair, plus fill marshaling). Under a bounded queue a failed `TryWrite` (queue full) leaves `tcs` **never completed → deadlock**. Bounding the `EventQueue` therefore requires converting these marshaling writes to `await WriteAsync(..., ct)` (guaranteed enqueue), or an equivalent guaranteed-delivery path, while preserving the single-reader processing model and the `StopAsync` drain-before-cancel ordering. The order/cancel channels (`FullMode.Wait`) block the producer on full — acceptable for the order path (never dropped). Capacity tuning is **not** a unit-test perf assert; only correctness (no deadlock, no drop, drain ordering) is tested here.

## D. Archival wiring (the relay tee)

`RelayPumpHostedService` (in `LiveHost.WebApi`), for the configured collect-instrument set:

```
BinanceVenueConnector ──Stream──► RelayIngest.Pump ──► RelayWriter
                                                          │ StreamPipeline<TradeTick> (bounded, fsync-on-rotation)
                                                          ▼
                                              LocalSegmentSink  live-md/{venue}/{instrument}/trades/*.atft
                                                          │ SegmentUploader (marker-idempotent sweep)
                                                          ▼
                                                     IFileStorage
```

- `RelayWriter` owns the per-venue `_session` heartbeat (built, Plan 1). The pump emits `SessionEvent` `SessionStart`/`SessionEnd` on connect/disconnect so liveness boundaries are captured (the `_session` stream).
- The Plan-2 canonicalizer (HistoryLoader host, `Canonicalizer:Enabled`) tails the uploaded `.atft` and writes canonical partitions — **the round-trip closes across two hosts.** Plan 3 does not run or modify the HL host; the acceptance test exercises the canonicalizer directly (§F).
- Archival is independent of execution: a stalled `SegmentUploader` backs up only the bounded archival `StreamPipeline`, never the order path (§A invariant 2).

## E. Configuration & host

- `LiveHost.WebApi` `appsettings.{Profile}.json` (`ATF_PROFILE`: `local`|`binance`): `BinanceLive` accounts (existing), relay options (root path, rotation size, upload cadence, heartbeat interval), `Venue` id, and the **collect-instrument set with per-instrument price/qty scale**.
- Live DI extension registers: `BinanceVenueConnector`, `RelayWriter`/`LocalSegmentSink`/`SegmentUploader` (against the host `IFileStorage`), `RelayPumpHostedService`, the slimmed connector + `BinanceLiveAccountManager`, session store, and the CQRS handlers — moved verbatim where possible from the current shared DI.
- VS Code launch settings gain a LiveHost entry (authoritative ports per the project reference); CI builds the new projects.

## F. Testing & verification

- **TDD per task.** Opus implementers for: bounded `EventQueue`↔reconciliation (§C), connector WS lifecycle, archival pump + `_session` lifecycle (§D).
- **Acceptance test (open/closed round-trip):** a recorded/fake `aggTrade` source → `BinanceVenueConnector` → `RelayIngest.Pump` → `.atft` → `StreamCanonicalizer<TradeTick>` + `TradeProjection` → canonical tick CSV; assert lossless `ts,price,qty,is_buyer_maker,agg_id` and correct unscaling. Re-assert the Plan-2 open/closed proof (a new event type canonicalizes with zero canonicalizer edits). No live Binance credentials in CI; the live-handshake validation against Binance is **manual** (mirrors the IB-connector POC pattern).
- **Allocation:** run the Plan-1 1000-instrument BDN firehose (existing harness, `run-benchmarks` skill); record alloc/sec at the ingest seam and the **push/visitor upgrade trigger** (e.g. "switch to `IVenueSink.OnTrade(in TradeTick)` when seam allocation exceeds _N_ MB/s sustained, or at IB onboarding"). No ad-hoc timing asserts.
- **Bounded-channel correctness:** unit tests for no-deadlock under a full `EventQueue` during reconciliation, no order drop, and preserved `StopAsync` drain-before-cancel ordering.
- **Build/test gate:** full solution green; backtest `WebApi`/`Gateway` builds without any live reference; one `dotnet` process at a time.
- Final whole-branch opus review + the open/closed acceptance test as the close-out.

## G. The deferred push/visitor decision (now recorded)

`IVenueConnector` returns `IAsyncEnumerable<IMarketEvent>`; each event boxes an `IMarketEvent` carrier and switches on type **at the ingest seam** (once per event, not per frame). For Binance live (tens of instruments, a few `aggTrade`/sec each) this allocation is immaterial. The GC-free upgrade — a push/visitor `IVenueSink.OnTrade(in TradeTick)`/`OnQuote(in QuoteTick)` model that eliminates the boxing — matters only at the ~1000-instrument firehose (IB). The relay's per-frame hot path is already zero-boxing (compile-time generics); only the seam carrier is at issue.

**Decision (as built): ship the pull seam now; defer the push/visitor upgrade to a recorded trigger.** The trigger is *rate-driven, not number-driven*: the seam allocates exactly one boxed `IMarketEvent` (a sealed `record` reference, ≈ 24–32 B on x64) **per event**, garbage-collectable in Gen0. Analytic worst case at the IB target (≈ 1000 instruments × ~4 top-of-book updates/s ≈ 4000 events/s): ≈ 4000 × 32 B ≈ **~128 KB/s** of short-lived Gen0 traffic — trivially within the Gen0 budget, never reaching the archival or order paths (which are struct-based and already zero-boxing). **Concrete upgrade trigger: adopt the push/visitor `IVenueSink` model at IB (single-session venue) onboarding, OR earlier if a clean-machine firehose measurement shows sustained seam allocation exceeding ~50 MB/s** (a Gen0-pressure heuristic), whichever comes first. Until then the pull seam stays.

**Measurement note:** the precise firehose allocation figure is to be captured with the existing BenchmarkDotNet harness (`benchmarks/AlgoTradeForge.Benchmarks/Benchmarks/RelayBenchmarks.cs`, the 1000-instrument relay firehose) via `scripts/perf/save-baseline.ps1` **on a quiet machine** — the constitution's benchmark pre-flight forbids measuring under `dotnet` contention, and the decision above (defer to IB onboarding) does not depend on the exact number, so the measured value is recorded as a follow-up rather than gating this plan.

## H. Risks / open points

1. **Bounded `EventQueue` deadlock (§C)** — the load-bearing correctness risk; covered by a dedicated opus task + full-queue reconciliation test.
2. **Connector/execution touch the working Binance path** — mitigated because the path is unused; existing live tests are migrated to the new projects and updated for the seam split (bar-delivery tests move to Plan 4 or are marked deferred, not silently deleted).
3. **`SessionStart`/`SessionEnd` placement** — emitted by the pump wrapper around connect/disconnect; gap interpretation (data-stream gaps vs `_session`) stays the consumer's concern (Plan-2 design).
4. **Two market-data subscriptions interim** — Plan 3's ingest connector is the only live market-data consumer (execution no longer subscribes klines); no duplication, since bar-delivery is deferred, not duplicated.

## Verification

This is an architecture/design document. It is "done" when the owner signs off on the project layout, the ingest/execution seam split (and the intentionally-severed bar delivery), the bounded-channel approach, the archival wiring, the config/host model, and the recorded push/visitor decision. Implementation verification lands in the per-task plan: the round-trip acceptance test (recorded `aggTrade` → `.atft` → canonical CSV, lossless), bounded-channel correctness tests, allocation measured via the BDN firehose, and a green full solution with the backtest host fully de-referenced from live code.
