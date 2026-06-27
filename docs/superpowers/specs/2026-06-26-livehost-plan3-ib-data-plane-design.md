# LiveHost — Plan 3: IbVenueConnector (data plane) + single-session IbSession — Design

**Date:** 2026-06-26
**Status:** Approved (brainstorming) — ready for writing-plans
**Branch:** `feat/livehost-plan3-ib-data-plane` (off `main`, which contains merged Plan 1)
**Spine position:** Plan 3 of the IB re-plan (`1 → {2,3} → 4 → 5`); depends on Plan 1 (merged). See `2026-06-25-livehost-ib-replan-phase-design.md` for the shared spine and the three locked cross-cutting decisions (data-seam bridge, contract identity, single-session).

## Scope

Build the Interactive Brokers **market-data plane** for LiveHost, driving toward a working `LiveHost@ib` (paper) that collects IB ticks end-to-end. Plan 3 owns:

- **`IbSession`** — the shared per-venue session that owns the one `EClientSocket` + `EReader` pump, grown **around** the Plan-1 `IbConnection` + `IbWrapper`. The data plane (this plan) and the order plane (Plan 4) both hold a handle to it.
- **`IbVenueConnector : IVenueConnector`** — bridges `EWrapper` push callbacks (`tickByTickAllLast → TradeTick`) through a **bounded channel** to the existing pull seam `IAsyncEnumerable<IMarketEvent>`. Relay archival + tick-router dispatch via the untouched `RelayIngest.Pump`.
- **`IbVenueBarSource : IBarSource`** — venue-published 5s bars via `reqRealTimeBars` ("TRADES"), resolved by an IB-aware `IBarSourceResolver`, fed to dispatch — mirroring `KlineVenueBarSource`.
- **Reconnect + catch-up + real historical backfill** — lossless IB reconnect recovery via a time-based catch-up gate plus `reqHistoricalTicks` gap-bridging (`IbBackfillRequester`).
- **Host wiring** — a `Venue` config key that selects the IB trio vs the Binance trio; `MarketDataSessionPolicy` host wiring (IB = `SingleSession`, Binance = `Concurrent`, unchanged).
- The three carried Plan-1 residuals (pump Join, `nextValidId` reconnect, faulted-not-cached resolver test), validated here where reconnect actually cycles the transport.

**Owner directive (unchanged):** NOT in production. Break for the cleanest end-state — no back-compat shims. "Clean" = correct + tested. All IB types `internal`; the `IBApi` reference stops at the connector/translation seam. Domain stays venue-neutral with zero new ProjectReferences.

## Locked decisions (this brainstorming)

| # | Decision | Rationale |
|---|----------|-----------|
| 1 | **Venue-bar lane is a separate `IbVenueBarSource`** (not a new `IMarketEvent` through `Stream`). | Mirrors the existing `KlineVenueBarSource`/`BinanceVenueConnector` split; keeps the relay seam trade-only and `RelayIngest.Pump` + `IMarketEvent` kinds untouched (the phase spec's "seam untouched" lock). |
| 2 | **Full reconnect + real `reqHistoricalTicks` backfill** (option C). | Closes the replan memo's flagged "unsolved" edge: time-based gate + historical backfill = **lossless** reconnect recovery despite IB carrying no `aggId`. |
| 3 | **Config-driven single active venue** (`Venue` key). | Aligns with the service-decomposition vision's "LiveHost = N instances by venue class"; symmetric with today's single-`IVenueConnector` registration. Plan 5 maps `ATF_PROFILE → Venue`. |
| 4 | **Configured per-instrument scale exponents** (mirror `BinanceVenueConnector`). | `IVenueConnector.InstrumentScale` is sync and called before `Stream`; minTick-derived scale would force a startup pre-resolution pass. minTick-derivation deferred. |
| 5 | **`Aggressor = Unknown`, synthetic monotonic `Sequence`.** | IB `tickByTickAllLast` has no maker/taker flag and no exchange sequence id. The "tick test" inference is lossy and path-dependent (would break replay determinism), so it is declined. The synthetic sequence is for archive ordering/observability only — **not** a gap-detection signal (decision 2 uses time + historical backfill, not a sequence watermark). |
| 6 | **`IbSession` owns reconnect + re-subscribe**; `IbConnection` stays the raw transport; the reqId→sink demux lives in `IbWrapper`. | "Grow around `IbConnection`"; `IbWrapper` IS the `EWrapper` and already holds the `_byReq` correlator, so a second market-data correlator is the natural home. |

## Architecture

```
                          ┌─────────────── IbSession (shared, internal) ──────┐
                          │  IbConnection (Plan-1 transport: 1 EClientSocket   │
                          │   + EReader pump)   +   IbWrapper (1 callback sink)│
                          │  reqId allocator · subscriber registry · reconnect │
                          └───────┬───────────────────────┬───────────────────┘
       TICK LANE (relay)         │                        │     BAR LANE (dispatch)
  reqTickByTickData "AllLast" ───┘                        └─── reqRealTimeBars 5s "TRADES"
        tickByTickAllLast                                        realtimeBar
              │                                                      │
   IbVenueConnector.Stream                                   IbVenueBarSource : IBarSource
   (push→bounded channel→pull)                               (realtimeBar → Int64Bar → onBar)
              │                                                      │
   RelayIngest.Pump  ── archival .atft ──▶ canonical CSV       dispatch.DispatchBar
              └── tap ──▶ TickRouter.Publish ──▶ alt-bar aggregation + raw-tick dispatch
```

Both lanes resolve their `IbContract` via the Plan-1 `IbContractResolver` and issue requests on the **one** `IbSession` socket. `IbWrapper` demuxes every callback back to the right lane by `reqId`. `RelayIngest.Pump`, `RelayPumpHostedService`, `TickRouter`, and the `IMarketEvent` seam are **untouched** — IB enters through the same doors Binance does.

### IB callback / request signatures (verified against vendored IBApi 10.45.01)

```csharp
// EWrapper callbacks (fire on the single EReader pump thread)
void tickByTickAllLast(int reqId, int tickType, long time, double price, decimal size,
                       TickAttribLast tickAttribLast, string exchange, string specialConditions);
void realtimeBar(int reqId, long date, double open, double high, double low, double close,
                 decimal volume, decimal WAP, int count);
void historicalTicksLast(int reqId, HistoricalTickLast[] ticks, bool done);
void connectionClosed();
void error(int id, long errorTime, int errorCode, string errorMsg, string advancedOrderRejectJson); // id == -1: connectivity notice

// EClient requests (issued on the shared socket)
void reqTickByTickData(int requestId, Contract contract, string tickType, int numberOfTicks, bool ignoreSize);
void reqRealTimeBars(int tickerId, Contract contract, int barSize, string whatToShow, bool useRTH, List<TagValue> realTimeBarsOptions);
void reqHistoricalTicks(int reqId, Contract contract, string startDateTime, string endDateTime,
                        int numberOfTicks, string whatToShow, int useRth, bool ignoreSize, List<TagValue> miscOptions);
```

**Time-unit note:** `tickByTickAllLast.time`, `realtimeBar.date`, and `HistoricalTickLast.Time` are **Unix seconds**; `TradeTick.TimestampMs` and `Int64Bar.OpenTime` are **milliseconds** → ×1000 at the boundary.

## Components

### 1. `IbSession` (new `internal`)

Composes `IbConnection` + `IbWrapper`. Responsibilities:

- **reqId allocation:** `Interlocked.Increment` over one counter shared by all lanes (and, later, Plan 4 orders).
- **Subscribe seam (typed):**
  - `SubscribeTrades(ResolvedIbContract) → ChannelReader<TradeTick>` — allocates a reqId, registers a tick sink in `IbWrapper`, issues `reqTickByTickData(reqId, contract, "AllLast", 0, ignoreSize:false)`.
  - `SubscribeRealtimeBars(ResolvedIbContract, Action<Int64Bar,bool> onBar)` — allocates a reqId, registers a bar sink, issues `reqRealTimeBars(reqId, contract, 5, "TRADES", useRTH:false, null)`.
- **Reconnect lifecycle:** on a drop signal from `IbWrapper` (`connectionClosed` / error 1100), re-run `IbConnection.Connect`, then **re-subscribe every active subscription** (the session tracks its live `(reqId → request descriptor)` set), then trigger catch-up on each affected source.
- **Single-session policy carrier:** the session is the object both planes share; in Plan 3 the two data sub-lanes (ticks + bars) share it. Plan 4 adds the order sub-lane to the same session.

### 2. `IbWrapper` growth

Adds a **second** correlator alongside the existing contract-details `_byReq`: a `reqId → IMarketDataSink` registry that `IbSession` installs into. New overrides:

- `tickByTickAllLast` → look up the sink, build a `TradeTick` (scaled, seconds×1000, `Unknown`, synthetic seq), `TryWrite` to its bounded channel.
- `realtimeBar` → look up the sink, build an `Int64Bar` (scaled OHLC, `date`×1000, `volume` via `MoneyConvert.ToLong`), invoke its `onBar`.
- `historicalTicksLast` → route to the backfill correlator's awaiter (accumulate + complete on `done`).
- `connectionClosed` + `error(id == -1, code 1100/1102)` → raise the session's reconnect trigger.
- **Residual (b):** `_nextValidId` is made resettable per connect — a fresh `TaskCompletionSource<int>` is installed at the start of each `Connect` attempt so a reconnect's second `nextValidId` is consumed rather than dropped by `TrySetResult` on an already-completed source.

### 3. `IbConnection` — residual (a)

`Disconnect()` / `DisposeAsync()` now actively `issueSignal()` to wake the parked pump thread, then `Join(timeout)` it. Required because reconnect cycles the pump repeatedly; "parked till process exit" is no longer acceptable. The Plan-1 fresh-signal-per-attempt and `Teardown`-in-`finally` invariants are preserved.

### 4. `IbVenueConnector : IVenueConnector` (tick lane)

Mirrors `BinanceVenueConnector`:

- `Venue => "ib"`, `SessionPolicy => MarketDataSessionPolicy.SingleSession`.
- `InstrumentScale(instrument)` → configured per-instrument `(PriceExp, QtyExp)` from options with a default fallback (mirror `TickScale` + `InstrumentScales`).
- `Stream(instruments, ct)`:
  1. For each instrument: `Asset → IbContract` (Plan-1 `IbContractMapping`) → `IbContractResolver.Resolve → ResolvedIbContract`.
  2. `IbSession.SubscribeTrades` per instrument; merge each reqId's `ChannelReader<TradeTick>` into one bounded output channel; `yield return new TradeEvent(instrument, tick)` draining it (the push→channel→pull bridge).
  3. `TradeTick`: `Price = ScalePrice(price)`, `Quantity = ScaleQty(size)`, `TimestampMs = time*1000`, `Aggressor = Unknown`, `Sequence = synthetic per-instrument ++`.

### 5. `IbVenueBarSource : IBarSource` (bar lane)

Analogue of `KlineVenueBarSource`:

- `Start()` → `IbSession.SubscribeRealtimeBars(resolved, onBar)`.
- `realtimeBar` callback (via the wrapper sink) → `Int64Bar` (scaled OHLC, `date*1000`, volume via `MoneyConvert.ToLong`) → `onBar(bar, isStart)`; maintains a bounded `Recent` queue exactly like the kline source.
- IB realtime bars are **5s only** (IB's fixed grain). Larger time bars come from the tick-aggregation lane, not from here.

### 6. `IbBarSourceResolver : IBarSourceResolver`

Venue-specific resolver, selected by host config:

- `TimeBarSubscription` (5s) → `IbVenueBarSource`.
- `AltBarSubscription` → `TickAggregationBarSource`, **reusing** the venue-agnostic catch-up machinery wired with IB's replay source (`RelayArchiveReplaySource`, `venue="ib"`) + `IbBackfillRequester` (§7) and a time-based gate. Threshold frozen from the feed-id exactly like Binance.
- `TickSubscription` → `null`.
- Renko alt-bars stay catch-up-fenced (same follow-up as Binance — cross-bar `_pendingVolume` is not reconstructed by replay-from-last-completed-bar).

### 7. Reconnect + catch-up + historical backfill (decision 2)

- **Drop detection:** `connectionClosed` / error 1100 in `IbWrapper` → session reconnect trigger.
- **Reconnect:** re-run `IbConnection.Connect` (retry loop), re-subscribe every active instrument/bar, kick catch-up per affected source.
- **Catch-up:** reuse `CatchupCoordinator` with a **time-based** `ICatchupGate` (no `aggId` on IB) replaying archived ticks from the last completed bar to rebuild the partial bar (the free by-product).
- **`IbBackfillRequester : IBackfillRequester`** (new) implements the unchanged Application seam. On a gap `[lastArchivedTs … now]`: a bounded, pacing-aware loop of `reqHistoricalTicks(whatToShow:"TRADES", numberOfTicks ≤ 1000, useRth:0, ignoreSize:false)` → `historicalTicksLast` → emit canonical `TradeTick`s → `CatchupCoordinator` dedups at the boundary by timestamp → **lossless stitch**. This is what makes IB reconnect lossless and closes the replan memo's flagged edge.

### 8. Host wiring (`Program.cs`)

A `Venue` config key (`"binance"` default | `"ib"`) selects, at registration time, **one** of two trios:

- **Binance** (unchanged): `BinanceVenueConnector` + Binance `BarSourceResolver` + `BinanceWebSocketManager`.
- **IB:** `IbVenueConnector` + `IbBarSourceResolver` + one shared `IbSession` singleton (+ `IbConnection`, `IbWrapper`, `IbContractResolver`, `IbConnectionContractDetailsClient`) + `IbConnectionOptions` (host/port `:4004`/clientId/scales). `IbBackfillRequester` replaces `BinanceBackfillRequester` for the catch-up wiring; `RelayArchiveReplaySource` is reused with `venue="ib"`.

`MarketDataSessionPolicy` is read from the resolved connector. One active venue per host process. `RelayPumpHostedService`, `TickRouter`, `TickRouterTradeTap`, `StrategyDispatch` are venue-agnostic and unchanged.

## Data flow (tick lane, end-to-end)

1. `RelayPumpHostedService` resolves the active `IVenueConnector` (IB) and calls `RelayIngest.Pump`.
2. `Pump` calls `connector.InstrumentScale(i)` (sync, configured) to register each instrument's scale with the `RelayWriter`, then `await foreach`-es `connector.Stream`.
3. `IbVenueConnector.Stream` resolves contracts, subscribes tick-by-tick on the shared `IbSession`, and yields `TradeEvent`s drained from the bounded bridge channel.
4. Each `TradeEvent` is archived losslessly (`.atft`) **and** tapped to `TickRouter.Publish`, which feeds tick-aggregation alt-bar sources and fans raw ticks to dispatch.
5. The archived `.atft` is later canonicalized by `StreamCanonicalizer<TradeTick>` to row-exact CSV (the verification target).

## Testing & verification

**TDD throughout.** xUnit1051 is an error → every awaited test call passes `TestContext.Current.CancellationToken`. NSubstitute on internal seams uses the existing `InternalsVisibleTo("DynamicProxyGenAssembly2")`.

- **Unit:**
  - `IbWrapper` reqId demux — ticks, bars, and historical ticks route to the correct registered sink; `connectionClosed`/1100 raises the reconnect trigger; residual (b) reconnect `nextValidId` consumed.
  - `IbVenueConnector` tick mapping — scale, seconds→ms, `Unknown` aggressor, synthetic monotonic sequence; bounded-channel drain order.
  - `IbVenueBarSource` — `realtimeBar → Int64Bar` (scale, seconds→ms, volume), `Recent` maintenance.
  - `IbBarSourceResolver` — TimeBar→bar source, AltBar→aggregation w/ IB catch-up, Tick→null, Renko fenced.
  - `IbBackfillRequester` — gap `[from…now]` → `reqHistoricalTicks` loop → canonical ticks → dedup/stitch at boundary (fully mockable over an internal historical-ticks client seam).
  - Reconnect re-subscribe — session re-issues all active subscriptions after a simulated drop.
  - **Residual (c):** faulted `IbContractResolver` resolution is **not** cached (a second attempt re-fetches).
- **Row-exact canonical CSV:** `IbRoundTripTests` mirroring `LiveRoundTripTests` — IB ticks → relay `.atft` → real `StreamCanonicalizer<TradeTick>` → canonical CSV asserted byte-exact (header + scaled rows).
- **Gated paper integration** (`[Trait("Category","IbPaper")]`, skip when `IB_PAPER_HOST` / `IB_PAPER_PORT` (4004) / `IB_PAPER_CLIENT_ID` unset): connect → resolve AAPL (STK) + GC (FUT front-month) → tick stream + 5s realtime bars against the gnzsnz paper gateway. The **lossless historical-backfill live assertion is gated/skipped** pending market-data entitlement (POC hit `10189 no subscription`); the requester is unit-tested regardless.

## Build / test gotchas (carry)

- ONE `dotnet` process at a time; `powershell.exe`, never `pwsh`.
- No `Async` suffix on new async methods; one type per file (private nested types OK); using-over-try/finally; no `catch when (ex is not OperationCanceledException)` in long loops — use `IsTrueShutdown`.
- Int64 money: `MoneyConvert.ToLong` in Domain; the IB connector does **independent** price/qty scaling at its own boundary.
- Every channel bounded; the bar lane is independent of the tick lane; the (future) order path stays independent of market data.
- All IB types `internal`; `IBApi` reference confined to the connector/translation/wrapper seam.
- Vendored IBApi 10.45.01 facts: Google.Protobuf 3.29.5, nullable-off lib at `src/AlgoTradeForge.IbApi`; version-sensitive callbacks; paper port socat `:4004`; unique `clientId` per connection.

## Verification (plan-level "done")

- IB ticks → relay `.atft` → real `StreamCanonicalizer<TradeTick>` → canonical CSV **row-exact** (mirrors `LiveRoundTripTests`).
- Venue-published 5s bars resolve via `IBarSourceResolver` and reach dispatch.
- Reconnect re-subscribes and (unit-level) lossless-stitches a gap via `IbBackfillRequester`.
- Host boots as `LiveHost@ib` under `Venue=ib`; `MarketDataSessionPolicy.SingleSession` wires the shared `IbSession`.
- Full LiveHost.Infrastructure + WebApi suites green; Domain untouched (zero IB vocab, zero new ProjectRefs); build 0/0.
- Residuals (a)/(b)/(c) landed and tested.

## Open points (deferred, flagged)

1. **Market-data entitlement** (`10189`) — live historical-backfill lossless assertion can't run on the current paper AAPL feed; unit-tested + live-gated.
2. **True mid-session silent loss** is now closed by historical backfill, but only as far back as IB historical depth + pacing allow; documented.
3. **minTick-derived scale** — deferred (sync-seam timing); configured exponents for now.
4. **Renko catch-up** — fenced (same path-dependent `ReplayBoundary` follow-up as Binance).
5. **Order plane (Plan 4)** shares this `IbSession` socket — the session's subscribe/reconnect seam is designed to accept an order sub-lane without reshaping.
6. **Multi-currency PnL** — out of scope for the USD paper endpoint (carried from the phase spec).
