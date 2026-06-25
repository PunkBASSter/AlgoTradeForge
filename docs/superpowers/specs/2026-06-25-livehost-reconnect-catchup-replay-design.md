# LiveHost — Live Reconnect / Catch-up Replay — Design

**Date:** 2026-06-25
**Status:** Approved (brainstorming) — feeds the Plan 3 brainstorm; standalone venue-agnostic mechanism
**Scope:** The mechanism that brings a live alt-bar data plane to the present — at cold session start **and** after a dropped connection — by **replaying archived source records** through the existing aggregation engine, rather than persisting derived accumulator state. Mid-bar (partial) continuity for event-driven alt-bars (vision **M6**) falls out as the terminal state of that replay. Venue-agnostic (Binance + IB); the IB data-plane specifics (push→channel bridge, single-session) are consumed by **Plan 3** of the IB re-plan phase (`2026-06-25-livehost-ib-replan-phase-design.md`), which references this doc rather than re-deriving it.

## Context

**Why replay, not persist.** A prior plan (`2026-06-25-livehost-m6-partial-bar-seeding-design.md`) closed M6 by *persisting* mid-bar accumulator state (a polymorphic `TrySaveState`/`RestoreState` seam + typed Domain records + JSON codec + `IFileStorage` store). It was implemented (10 commits) then **reverted**: the partial bar is redundant with the already-lossless tick archive (relay `.atft` → `StreamCanonicalizer<TradeTick>` → canonical CSV, with HistoryLoader as the deeper store). Anything the partial bar holds can be **recomputed** by re-feeding the archived source records since the last completed bar. The old Renko `long` resume seam (`SeedResumeState`/`TryGetResumeState` on `IBarAccumulator`) was restored unchanged and stays.

**Three findings from the code that shape this design:**

1. **There is no live history-seeding today.** `TickAggregationBarSource` constructs a fresh accumulator (`_barEmpty = true`) with an empty `Recent` ring; the strategy receives only bars derived from live ticks *after* session start. So this is not a partial-bar patch onto an existing handoff — it is the mechanism that first brings history into the live data plane at all. The partial bar is one record of its output, not a separate artifact.

2. **Bar sources are shared, not per-strategy.** `TickRouter` keys sources by `(instrument, BarSpecKey)` with a `RefCount`, reused across sessions. Overlapping subscriptions share **one** accumulator and one `Recent` ring; `DispatchBar` fans each emitted bar to every interested session. Recovery is therefore a property of the **shared source**, not the session — and a "disconnect" is a venue-connection event (one per-venue relay pump feeds all instruments), not a per-strategy event.

3. **The gap-detection signal already exists.** `RelayWriter` emits `SessionStart`/`Heartbeat`/`SessionEnd` into a `_session` stream alongside trades; `RelayIngest.Pump` fans every tick to both archival (lossless) and the dispatch tap (best-effort). Heartbeats distinguish "no trades happened" from "we were disconnected."

**Owner directive (unchanged):** NOT in production — break for the cleanest end-state, no back-compat shims. "Clean" still means correct + tested: the `BatchEqualsLiveGoldenTests` golden and the live data-plane tests are the behaviour guard.

## Locked decisions (this brainstorming)

1. **Unified seed + catch-up.** One replay mechanism handles both cold warmup→live seeding (large window) and mid-session reconnect gap-fill (small window). Cold start is "replay with a large window"; reconnect is "replay with a small window." The partial bar falls out of both.
2. **Two record streams, one accumulator.** The *N completed warmup bars* the strategy needs for indicator history are **read** from HistoryLoader's already-aggregated alt-bar feed (they *are* the batch bars — the golden holds by construction; not re-derived). Source-record **replay** covers only the **tail** from the last completed bar's open forward.
3. **Contiguity via a monotonic source watermark.** Replayed records and live ticks enter the accumulator through one gate that tracks the last-consumed source identity (aggId, ts fallback) and drops anything `<=` it. Catch-up and live become a single ordered stream; the overlap self-dedupes; a true gap shows up as a watermark **jump**. (Chosen over a phased pre-roll/attach barrier: it reuses the batch side's existing `MonotonicTickSource` ordering invariant and turns coordination into ordering — stateless-per-record, idempotent, degrades gracefully.)
4. **Gap policy = bounded-wait-then-declare (per-venue budget).** On a true gap (records missing from relay AND archive): emit a `Discontinuity` marker, request inline backfill, poll up to a per-venue `BackfillBudget`. Filled → replay continues contiguous (no discontinuity surfaced). Expired → reset the accumulator at a clean boundary (fresh bar at first contiguous live tick) and go live. Binance budget generous (REST backfill closes it); IB budget ≈ 0 (tick-by-tick history unrecoverable) → always falls back to declare-and-continue. Same code path; the venue difference is the budget value.
5. **Shared recovery seam, separate impls.** Market-data catch-up and M3b order-state recovery share a small vocabulary (`Discontinuity{FromTs, ToTs, Reason}` — **time-based and venue-agnostic**; the gap *detection* mechanism is venue-specific and encapsulated, see below — `RecoveryPolicy{BackfillBudget}`, the detect→request→bounded-wait→reconcile→resume contract). They do **not** share reconcile logic: market data reconciles a watermark+accumulator; orders reconcile via `OrderGroupReconciler` + exchange query.
6. **Identity alignment by mapping, not unification.** The live dispatch plane keeps `instrument == AssetName` as its routing key (untouched). The replay-source locator takes the resolved `Asset` (already obtained in `StartLiveSessionCommandHandler`) and derives the on-disk location via the existing `AssetDirectoryName.From(asset)`. One-way deterministic mapping at the replay boundary; no hot-path change.

## Layering (venue-agnostic abstraction → optional shared base → venue-specific impl)

```
Domain (zero ProjectReferences):
    IBarAccumulator                  — unchanged; restored Renko long resume seam stays.
                                       Replay never inspects accumulator internals.

LiveHost.Application/Live/Recovery/  — venue-agnostic abstractions + shared base
    Discontinuity, DiscontinuityReason
    RecoveryPolicy { BackfillBudget }
    IReplaySource                    — yields SourceRecord by (Asset, sourceFeedId, fromTs), ts-ordered
    IBackfillRequester               — TryBackfill(gap, policy, ct): detect→request→bounded-wait
    WatermarkGate                    — monotonic source-identity dedupe (shared, venue-agnostic)
    CatchupCoordinator               — shared base: seed warmup, run replay loop, drive the gate,
                                       apply gap policy B, suppress-known-bars, drain live buffer

LiveHost.Infrastructure/Live/        — venue-specific impls
    Binance/  BinanceBackfillRequester (REST aggTrade/kline; generous budget)
              IReplaySource impl: relay .atft reader + shared-Infrastructure canonical CSV read-side
    Ib/       IbBackfillRequester (budget ≈ 0 → falls back to declare-discontinuity)
```

`CatchupCoordinator` and `WatermarkGate` are the "optional base" — the genuinely reusable logic both venues share. Only the *backfill source* (REST shape, tick limits, budget) and the *replay byte source* are venue-specific.

**Constraint respected:** the canonical-archive read-side is the **shared `AlgoTradeForge.Infrastructure`** loaders (`NewFormatBarLoader`/`CsvFeedSeriesLoader` family + `AssetDirectoryName`), **not** `HistoryLoader.Application` — LiveHost must not depend on HistoryLoader. The relay `.atft` reader is the one new read component and reuses the relay's existing frame-decode (`IFramePayload`) rather than re-parsing.

**Plug-in point:** `TickAggregationBarSource` becomes catch-up-aware. Its `IBarSource.Start()` (already awaited in `TickRouter.EnsureSources`) runs the coordinator. The hot `Feed` path gains a `Catching-up` (buffer into a bounded queue) vs `Live` (direct to accumulator) state. `TickRouter`, `StrategyDispatch`, and `RelayIngest` are otherwise untouched.

## Resume boundary

The clean, re-startable boundary is the **last completed bar's open ts**, available per feed in the alt-bar manifest as `AltBarFeedSpec.LastBarTs`.

Note the manifest does **not** literally store "last completed bar's last consumed source ts": `Source.LastTs` is the end of the whole consumed stream *including the discarded trailing partial*, not the last completed bar's close. `LastBarTs` (the bar's *open*) is the correct boundary anyway — replaying source records from `LastBarTs` forward re-derives the last completed bar deterministically (then the partial), so the close ts is not needed.

- **Cold start:** boundary = the persisted feed's `LastBarTs`.
- **Reconnect:** boundary = the last bar this shared source actually emitted (held in memory), so the window is genuinely "one partial bar's worth" rather than "since the feed was built."

## Data flow

**Cold start** (per shared source, on first creation in `EnsureSources` → `Start()`):

```
1. boundary  := alt-bar manifest LastBarTs
2. warmup    := read last N completed bars from the persisted alt-bar feed
                → fill Recent ring; drive strategy indicator warmup (NOT re-derived)
3. connect   → live ticks begin; relay archives them; source state = Catching-up
                → Feed() BUFFERS into a bounded queue
4. replay    := IReplaySource.Read(asset, sourceFeedId, fromTs = boundary)
                each SourceRecord → WatermarkGate → accumulator:
                  · emit with bar.TsMs <= boundary → SUPPRESS (known last bar;
                    assert == persisted bar → inline batch≡replay golden)
                  · emit with bar.TsMs  > boundary → DISPATCH (genuinely new)
                  · stream ends → trailing partial resides in the accumulator
5. drain     → buffered live ticks → same WatermarkGate → accumulator
                (replay/live overlap self-dedupes: id/ts <= watermark dropped)
6. go live   state = Live → Feed() direct to accumulator
```

**Reconnect** (per affected shared source, on connection-drop signal): identical, except step 2 is skipped (`Recent` already warm) and the boundary is the source's last-emitted bar.

**Replay source selection & stitching** — `IReplaySource` reads ts-ordered and picks per record by where the data lives:
- **Recent tail** → relay `.atft` segments (no canonicalization lag; covers `[relayRetentionStart, now]`).
- **Deeper** → canonical archive via the shared `Infrastructure` read-side.
- Boundary = relay's earliest retained segment; records straddling it pass through the same `WatermarkGate`, so any overlap dedupes for free.

## Gap detection & true-disconnect (policy B)

**Signal:**
- **Binance:** relay `SessionStart`/`Heartbeat`/`SessionEnd` + the source-ts watermark. Heartbeats present, no trades, watermark not advancing = quiet market (no gap). Heartbeat gap and/or watermark **jump** across reconnect = candidate gap.
- **IB:** connection-state callbacks (`connectionClosed`/`error`) + the same watermark-jump confirmation.
- A candidate gap is a **true** gap only when `IReplaySource` cannot supply records to bridge the jump (relay + archive both empty for `[from,to]`).
- **Detection vs marker (venue layering):** the *signal* is venue-specific and stays inside the detector — Binance's `WatermarkGate` decides a gap from aggId discontinuity; IB decides from connection-state callbacks + a time window. The *marker* that crosses the venue boundary (`Discontinuity`) carries only `{FromTs, ToTs, Reason}` — the one descriptor every venue and every consumer (backfill REST `startTime/endTime`, HistoryLoader heal, FE) shares. The aggId never leaves the gate; time-range backfill self-corrects because replay re-reads through the same aggId watermark, which dedupes the overlap.

**Policy B** on a true gap found mid-replay:

```
emit Discontinuity{from, to, reason}
→ IBackfillRequester.TryBackfill(gap, policy)            // venue-specific
    poll up to RecoveryPolicy.BackfillBudget:
      filled  → records now in archive → replay continues contiguous (no Discontinuity surfaced)
      expired → re-open a fresh accumulator (AccumulatorEntry.Open) at a clean boundary
                (fresh bar at first contiguous live tick); Discontinuity stays on the
                record; session goes live
```

The coordinator owns the accumulator instance, so "reset at a clean boundary" is a fresh `AccumulatorEntry.Open` — no new `IBarAccumulator` method, consistent with "replay never inspects accumulator internals." Budget=0 makes B behave as "declare and continue." Healing the historical record (HistoryLoader re-aggregation once it backfills) is a separate, offline concern; the seam (`Discontinuity` + `IBackfillRequester`) is built now.

**Edge cases.** A `Discontinuity` is a **closed interval between two observed ticks** (`FromTs` = last consumed tick, `ToTs` = first tick after the gap) — never an open-ended "…till now." Because the live connection is opened and buffered *before* replay, the gap's far edge is pinned to a specific buffered reconnect tick; the still-growing present accumulates in the (bounded) live buffer and drains after the bridge, so a slow/long catch-up faces a bigger drain, not a moving target. If that buffer overflows during a long catch-up, the dropped live ticks are still in the relay archive, so replay picks them up (open point #1) and the watermark dedupes — catch-up always converges. **Multiple gaps** in one window are each backfilled once, keyed by the gap's low boundary; a backfill that reports success without actually closing a gap is declared (not retried), so catch-up always terminates.

## Multi-strategy / shared-source semantics

Recovery is keyed to the shared `(instrument, BarSpecKey)` source, deduped across strategies by the `TickRouter` RefCount model:

- **Overlapping subscriptions (same feed):** one shared source → catch-up runs **exactly once**; all strategies receive identical re-derived/dispatched bars and the identical partial continuation. No double-replay, no divergence — they read the same accumulator and watermark.
- **Different feeds:** each is a distinct shared source with its own accumulator, watermark, and resume boundary; reconnect triggers an independent replay per affected source. Cost = (distinct shared feeds) × one-partial-each, not × strategies.
- **New session joins a running source:** `RefCount++` only; no re-trigger. The newcomer warms its own strategy from the persisted feed + the live source's `Recent`, without touching the shared accumulator.

**Two guards:**
1. **Per-source recovery latch (single-flight):** the connection-drop signal runs recovery once per source; sessions joining/leaving mid-recovery (under `TickRouter._lifecycleLock`) must not re-enter replay or feed the accumulator twice.
2. **Mid-recovery joiner:** a session that `RefCount++`s a source while it is recovering warms from the stable persisted feed immediately and attaches to the live-contiguous tail once the latch completes — it never reads a half-seeded `Recent`.

## Extensibility (new alt-bar types — CumSum and beyond)

Catch-up reconstructs the partial by re-feeding source records through the accumulator's existing `TryAdvance`. There is **no serialized state** to version, encode, or resolve by type-code — so a new bar type needs **nothing new** for catch-up beyond what it already needs to exist (its accumulator class + its one `AccumulatorEntry.Open` case). The recovery system is agnostic to what a bar is; all sophistication lives inside `TryAdvance`.

The only per-type variable is *where* the clean resume boundary is — a small `ReplayBoundary` declaration, not a codec:

| Type class | Clean resume boundary | Extra work |
|---|---|---|
| **Reset-at-bar** — EqV/EqT/EqD, imbalance trio, Range, **CumSum**, most event bars (accumulator resets to empty each bar) | last completed bar's open (`LastBarTs`) | **none** — the default |
| **Path-dependent** — Renko, hypothetical future types carrying cross-bar state | last completed bar **+** a small cross-bar anchor | declare the anchor; Renko already persists `LastBrickClose` in the manifest. If the cross-bar state can't be summarized, declare "replay from K bars back" — the suppress-emits-until-`LastBarTs` rule replays a few extra completed bars to rebuild state. One scalar `K`, no serialization. |

Contrast with the reverted persist approach, which required per type: an `XxxBarState` record, a `StateType` switch case, `Int128`↔string encoding, and a round-trip test. Replay couples a new type only to its own `TryAdvance` determinism — a property it is already tested for.

## FE live observation (enabled, deferred — out of scope here)

The FE today **polls** `GET /live/{id}/data`, which assembles bars from the shared sources' `Recent` rings (`SessionSnapshotBars`) + REST-backfilled klines — i.e. it already reads exactly what the strategy reads. Live *streaming* (push) to the FE is enabled by this architecture and explicitly not foreclosed:

- Shared-source model → "what my strategy sees" is the same accumulator the FE would read; no parallel aggregation.
- `DispatchBar`/`DispatchTick`/relay-tap fan-out is already multi-consumer; an FE observer is one more **best-effort** consumer (per-client bounded, drop-newest), off the correctness path — never able to backpressure the accumulator. Mirrors the relay's lossless-archival vs best-effort-dispatch split.
- Catch-up gives the FE the strategy's connect experience: seed the chart from `Recent` on connect (warm), then stream the watermarked live tail. The FE may observe a feed no strategy uses by becoming an observer-RefCount sharer, which can itself trigger catch-up.

**This design's only obligation:** do not couple `Recent` or the dispatch fan-out to a single consumer (it doesn't). The net-new piece for a future plan is a push transport (SSE/WebSocket) + per-client bounded drop-newest buffer seeded from `Recent`.

## Verification

The M6 golden (**batch ≡ replay ≡ live**) holds across catch-up by construction (shared engine + deterministic replay from a clean boundary):

- **Inline runtime golden:** every catch-up suppresses the re-derived bar at `LastBarTs` and asserts it equals the persisted bar — so the golden runs on every reconnect, not only in tests.
- **Restart golden ([Theory] over all 8 families + CumSum when added):** single uninterrupted batch run vs a run split inside an in-progress bar, replayed from `LastBarTs` through `IReplaySource` + `WatermarkGate`; assert element-wise `Int64Bar` equality across the seam.
- **Watermark dedupe:** replay/live overlap (duplicate ts/aggId) produces no double-counted records; an out-of-order record is dropped.
- **Gap policy B:** synthetic true gap → with a generous budget and a stub backfill that fills, replay is contiguous (no `Discontinuity`); with budget=0, a `Discontinuity` is emitted and a fresh accumulator is re-opened at a clean boundary.
- **Multi-strategy / shared source:** two sessions on the same `(instrument, spec)` → one replay (latch single-flight); identical dispatched bars to both; a mid-recovery joiner never observes a half-seeded `Recent`.
- **Relay round-trip unchanged:** mirrors `LiveRoundTripTests` (`.atft` → `StreamCanonicalizer<TradeTick>` → canonical CSV row-exact); the new `.atft` reader reads the same frames.
- **Identity mapping:** `AssetDirectoryName.From(asset)` resolves the live `instrument`/`Asset` to the correct on-disk source feed for replay (perp `_perp` tail).

## Open points (deferred, flagged)

1. **Live buffer sizing under backpressure** — the `Catching-up` buffer is bounded; on overflow during a long replay, fall back to extending the replay window from the archive rather than dropping (dropped live ticks are archived anyway). Concrete sizing/policy decided in writing-plans; relates to the four-planes §K spill-to-disk open point.
2. **Backfill request transport** — `IBackfillRequester` for Binance issues REST; whether it calls HistoryLoader's collection API or its own REST client (LiveHost must not depend on HistoryLoader.Application) is a Plan-3 wiring decision.
3. **Reconnect trigger source** — the exact connection-drop signal per venue (Binance WS close vs IB `connectionClosed`) and how it reaches the per-source recovery latch is detailed in Plan 3 (data plane) / Plan 4 (single-session).
4. **HistoryLoader heal cadence** — re-aggregation of a declared `Discontinuity` window once backfill lands is offline and owned by HistoryLoader; not scheduled here.
5. **FE push transport** — enabled, out of scope (see above); a future plan builds the SSE/WebSocket observer.
6. **M3b order-state recovery** — consumes the shared recovery vocabulary (decision 5) but its reconcile impl (`OrderGroupReconciler` + exchange query) is its own design.
