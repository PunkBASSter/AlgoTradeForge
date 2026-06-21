# LiveHost — Collection + Execution on Single-Session Venues — Design

**Date:** 2026-06-20
**Status:** Approved (brainstorming) — ready for implementation plan
**Scope:** Internal architecture of a `LiveHost@<venue>` instance for **single-session venues (IB first)**: how one process holds the broker connection(s) and simultaneously captures a lossless tick archive (up to ~1000 instruments, GC-free) *and* executes live strategies (tick/bar/event-triggered), with a seam to multi-account, multi-node scale.

## Context

**Why:** The service-decomposition vision (`docs/service-decomposition-vision.md`, Q7) resolves exchange connectivity as capability-driven. On **concurrent venues** (crypto) LiveHost and HistoryLoader connect independently. On **single-session venues** (Interactive Brokers, MT/dealer FIX — one login per account, market data and orders share the session) the session **must** belong to LiveHost, which relays raw market events to HistoryLoader for canonicalization. This document designs the *inside* of LiveHost for that single-session case — the only place the vision forces tick collection and strategy execution to cohabit one process.

The IB path is already de-risked by the connector POC (`docs/superpowers/specs/2026-06-18-ib-connector-poc-design.md`): `gnzsnz/ib-gateway` sidecar + .NET TWS API (`EWrapper` socket, 10.45.01, Protobuf), one login per account = the single-session constraint made concrete.

**Goal:** A design that lets `LiveHost@ib` (a) capture up to ~1000 instruments losslessly to mid-term (S3) storage for later backtest expansion, without GC freezes, and (b) execute up to ~20 strategies triggered by ticks, bars, or other events — with a no-rewrite path toward thousands of strategies across multiple accounts and nodes (potential SaaS / on-prem for props/funds/family offices).

**Decisions locked (this brainstorming):**
- **One process, internal isolation** — `LiveHost@ib` is a single process; collection and execution are separated by threads/bounded channels and a tuned GC config, not a process boundary.
- **Relay split by feed class** — tick feeds use a binary framed append log; low-rate order/fill/session events keep the vision's JSONL relay.
- **Shared accumulators, per-strategy delivery** — one `IBarAccumulator` per `(instrument, bar-spec)`, fanned out to each subscribed strategy's own bounded queue.
- **Extend `collection.json` with per-instrument roles**; strategy/account bindings stay in the live-session config (separate lifecycle).
- **Build the seam, not the split** — `IStrategyDispatch` (data in) and `IOrderRouter` (orders out) abstract in-process delivery now, cross-node delivery later.
- **Full multi-account model** — data source and order target are independent axes; single-account/single-node is the degenerate case.

## Out of scope (explicitly)

- **Concurrent venues (crypto):** unchanged — independent LiveHost/HistoryLoader connections per Q7. This design does not alter the Binance path except where the data-plane extraction (§C) is a shared refactor.
- **Cross-host canonicalization redesign:** HistoryLoader stays sole canonicalizer; this reuses the relay→cursor→partition contract, adding only a binary tick framing alongside JSONL.
- **Rejected Q7 alternatives stay rejected:** no exchange/connection proxy, no message broker for the relay (file-is-the-queue), no uniform "LiveHost collects everything" on concurrent venues.
- **The multi-node bus implementation itself** (Valkey/Garnet Streams) — only the seam is built now; the cross-node consumer is future work.
- **M3b recovery internals** are referenced, not re-specified here.

## A. Process & threading model — one process, four planes

A single `LiveHost@<venue>` process, internally partitioned into planes that share no locks on their hot paths:

```
 IB Gateway sidecar(s)  ──socket──►  [Ingest plane]      one reader task per connection
                                          │  normalize → struct TradeTick (pooled)
                          ┌───────────────┴───────────────┐
                          ▼                                ▼
                 [Archival plane]                  [Dispatch plane]      instrument-keyed
            bounded channel → binary               shared IBarAccumulators
            segment writer → S3 push               per (instrument, bar-spec)
            (all ~1000 instruments)                      │ fan-out (subset only)
                                                         ▼
                                                 [Execution plane]       account-keyed
                                                 per-strategy bounded queue
                                                 + processing task → IOrderRouter
```

Protection that a hard process boundary would give is bought back by two invariants:
1. **Every channel is bounded.** Today's `LiveOrderContext` and `BinanceLiveConnector` use *unbounded* `Channel`s — the one mandatory change. Unbounded buffering under a firehose is the GC-storm path.
2. **The order path never sits behind the archival path in any queue.** Ingest hands each normalized tick to the archival channel and the dispatch channel independently; a stalled S3 writer backs up only the archival channel, never strategy or order work.

**Cardinality asymmetry (the key enabler):** archival fans out *all* ~1000 instruments to the binary log; strategy dispatch fans out only the *executed subset* (tens of instruments). The heavy path never touches strategy code; the latency-sensitive path never carries the full firehose. They diverge immediately after normalization on the ingest thread.

**GC profile:** Server GC; `TradeTick` is a `readonly record struct`; ingest fills from `ArrayPool<TradeTick>`; the binary segment writer reuses a pinned buffer. Target = zero per-tick heap allocation on the ingest and archival paths. Reuses the codebase's existing hot-path discipline (`RingBuffer<T>`, struct `Int64Bar`, lock-free `Volatile` order status).

## A½. Canonical tick model — uniform internal, named external

Tick data is **not** uniform across asset classes, but the divergence splits cleanly, so the internal model stays uniform while external shapes are named explicitly. Three layers:

1. **Named external/source shapes** (per venue, parser-local): `BinanceAggTrade`, `IbLast` / `IbBidAsk`, OPRA/SIP shapes, etc. Each venue parser normalizes its DTO into a canonical struct — these names never leak inward.
2. **Canonical internal structs** (uniform, hot, GC-free; share the binary framing):
   - **`TradeTick`** `(TimestampMs, Price, Quantity, Sequence, AggressorSide)` — one executed print. Uniform across crypto/equity/futures/options. Aggressor side is asset-neutral `AggressorSide { Unknown, Buy, Sell }` (crypto `is_buyer_maker` maps in; equities default `Unknown`). The crypto-specific `TickFlags { BuyerMaker, Bid, Ask }` was replaced by this; `Bid`/`Ask` belonged to quotes, not trades.
   - **`QuoteTick`** `(TimestampMs, BidPrice, BidSize, AskPrice, AskSize, Sequence)` — the BBO snapshot, canonical form of the existing `bookTicker` feed. Its own binary stream, same header+frame machinery. *(Added with the first venue needing live BBO; not in the Plan-1 relay.)*
   - *(later)* a computed `GreekTick` / IV form for options, only when options go live.
3. **Asset-class metadata off the hot path**: trade-condition codes, exchange/tape, option greeks/underlying ride the `FeedSeries` sidecar channel keyed by sequence/time — never bloating the canonical struct.

This mirrors the bar pipeline's existing layering (`BinanceKlineMessage` → `Int64Bar` → `FeedSeries` sidecar), applied to ticks. The Plan-1 relay implements `TradeTick` (+ `AggressorSide`); `QuoteTick`/greeks are deferred.

**Venue-published bars (2026-06-21 addendum).** Some venues publish bars directly (e.g. a Binance 1m kline WebSocket). These drop into the open/closed relay as a **new `IFramePayload<T>` frame type** (`StreamName="bars"`, `Int64Bar`-shaped) — archived as their own `.atft` stream and canonicalized to the existing `candles/` feed with **zero canonicalizer edits** (the same drop-in proof Plan 2 demonstrated with `DepthTick`). A venue's published time-bar **is** the exchange's own truth for that exact spec; it is authoritative over a tick-aggregated reconstruction of the same spec. See §B for how the live data plane resolves bar source per `(instrument, bar-spec)`.

## B. Data plane — instrument-keyed dispatch

The market-data stream is **extracted out of the connector** (today `BinanceLiveConnector` owns both data and orders for one account) into a shared `ITickRouter`:

- A **market-data session** — the connection that holds the broker subscription (e.g. account A) — pushes normalized `TradeTick`s into the router keyed by **instrument**, not account.
- `IStrategyDispatch` (the seam) fans each instrument's ticks + completed bars to the subset of strategies subscribed to it. In-process implementation now = per-strategy bounded `Channel` + `SingleReader` processing task (today's per-session model, extended to carry ticks). Future implementation = a consumer reading the same instrument stream off Valkey/Garnet Streams on another node.
- **Shared accumulators:** one `IBarAccumulator` per `(instrument, bar-spec)` runs once on the tick stream; its completed bars are delivered to every subscribed strategy. Seeded at session start from the historical alt-bar feed + persisted partial-bar state (vision M6 parity: live bars ≡ historical bars).

**Live alt-bar aggregation — one engine fed twice (2026-06-21 addendum).** The alt-bar accumulator engine already exists as a **batch** layer: `IBarAccumulator.TryAdvance(in SourceRecord, out AggregatedBar)` + `Accumulators/` (EqV/EqT/EqD volume/tick/dollar bars, EqIV/EqID/EqIT imbalance bars with sidecars, Renko, Range) + `ThresholdResolver`/`StreamingMedianEstimator`, today in `AlgoTradeForge.HistoryLoader.Application/Aggregation/`. The contract is already streaming and **source-agnostic** — `SourceRecord` is documented as "a time-bar from `candles/` **or** a tick from `ticks/`", and `AggregatedBar` is the 6-long `Int64Bar` shape. So "aggregation happens twice" must mean **two drivers feeding one engine**, never two implementations:

- **Batch driver** (exists): `AggregationPipeline` + `PartitionedSourceReader` over archived partitions → stored alt-bar feeds (the warmup / long-term truth).
- **Live driver** (this plane, TODO): `IStrategyDispatch` feeds live ticks (via a trivial tick→`SourceRecord` adapter: `O=H=L=C=price`, `V=qty`) into the same accumulators → live alt-bar events.

Three decisions for the Plan-4/6 implementation:

1. **PREREQUISITE — extract the engine to a shared lib before Plan 4.** It currently lives in `HistoryLoader.Application` (host-coupled); LiveHost must not depend on the history host. Move the core (`IBarAccumulator`, `SourceRecord`, `AggregatedBar`, `Accumulators/`, `ThresholdResolver`) into a shared library (e.g. `AlgoTradeForge.Aggregation`, or fold into Domain) referenced by both hosts. Then the M6 golden property (batch ≡ replay ≡ live) holds **by construction**, not by parallel maintenance.
2. **Bar-source resolver.** For each subscribed `(instrument, bar-spec)`, resolve the bar **source**: a venue-published bar (§A½) if available and matching the spec, else tick-aggregation. Design the seam to treat "bar arrives from a feed" as a first-class source — do not assume every bar is tick-derived. Specs no venue publishes (volume/range/dollar/imbalance/Renko/ZigZag) always require tick-aggregation.
3. **Parity guards (M6).** (a) **Freeze thresholds:** alt-bar thresholds are derived from historical statistics (`ThresholdResolver`); resolve once at session start and freeze as a session parameter fed to the live accumulator — if live re-derives, live bars silently stop continuing the historical series. (b) **Seed partial-bar state:** seed the live accumulator with the mid-bar state from the warmup tail (the last historical bar may be incomplete), persisted as CAS JSON (§H), so the first live bar continues seamlessly.

## C. Execution plane — account-keyed order routing

Orders route by an `IOrderRouter` keyed by **account**, fully decoupled from the data source:

- Each account (A, B, …) has one order session = one broker connection. Account A's connection also carries market data; account B's connection carries orders only and **pays no market-data lines**, consuming A's instrument streams via the router.
- A strategy binding is two independent fields: `{ dataSubscriptions[], executionAccount }`. Strategy set X → `{ data: A, orders: A }`; set Y → `{ data: A, orders: B }`. Both read A's data; orders are isolated per account.
- `LiveOrderContext` becomes account-scoped; the existing 3-phase `OrderGroupReconciler` runs per order session.

**Single-account/single-node needs zero special-casing:** it is the general model with one account, where `dataSubscriptions` and `executionAccount` resolve to the same connection. The general form is built and tested; the simple case is configuration, not a separate code path.

## D. Strategy event model & interface changes

- Add a tick entry point — `OnTick(in TradeTick tick, DataSubscription sub)` on a new `ITickStrategy` (or an extension of `IInt64BarStrategy`), gated by an extended `LiveEventRouting` (`OnTick` flag added to the existing `OnBarStart | OnBarComplete | OnTrade`).
- `TickSubscription` (already a Domain type) becomes a first-class live subscription. Today `StartLiveSessionCommandHandler` rejects anything but `TimeBarSubscription` for live — that restriction is lifted once live bar-building (§B) and the tick path exist.
- Bar-triggered strategies are driven by the shared accumulators; tick-triggered strategies receive raw ticks; a single strategy may use both. "Other events" (fills, session boundaries) keep flowing through the existing `IEventBus` / `IOrderContext` receiver interfaces.

## E. Lossless capture & relay format

The relay is binary and **vertically sliced — one homogeneous stream per event type** (`trades`, `quotes`, …) plus a per-venue `_session` liveness stream. Full design (slices, generic codecs, on-disk layout): `docs/superpowers/specs/2026-06-20-livehost-relay-vertical-slices-design.md`. The invariants that matter at this (LiveHost) altitude:

- **Binary, fixed-width per stream** for market data (trades/quotes) — GC-free encode/decode via per-type generic codecs. **JSONL retained for the order/fill audit log** (low rate, inspectability). Liveness (heartbeat + session boundaries) is its own binary `_session` stream, not interleaved into data streams.
- **Losslessness is a durability property, not a format property:** append → `fsync` on segment rotation → bounded ingest channel with disk-backed spill, so a stalled S3 push applies backpressure but never drops or OOMs.
- **HistoryLoader is the sole canonicalizer:** it tails each stream with a persisted `{cursor}` under CAS (M6 incremental pattern) and writes canonical partitions + manifest entries; **LiveHost publishes raw, never touches partitions or `feeds.json`.**
- A `dump-ticks` CLI (codec-registry driven) restores human-inspectability of any binary stream.

**Volume sizing (IB streaming top-of-book ≈ 4 updates/sec/instrument, ~1000 instruments ≈ 4k updates/sec):** binary ≈ 128 KB/s ≈ ~11 GB/day raw (~2–3 GB zstd), near-zero ingest GC; JSONL would be ~4–5× the storage plus per-tick serialize/parse CPU and parse-side GC on HistoryLoader — which is why ticks are binary and only low-rate events stay JSONL. (IB does not deliver raw exchange tick-by-tick for 1000 instruments; market-data *lines* and `reqTickByTickData` limits make ~250 ms snapshots the realistic feed — a sizing input, see Risks.)

## F. Configuration model

- Extend the vision's CAS-protected `collection.json` (on `IFileStorage`, via `GET/PUT /api/v1/config`): each instrument entry gains `role: collect | collect+execute`. `collection.json` answers "what do we capture."
- **Strategy/account bindings** live separately, in the live-session start config / API — they have a start/stop lifecycle distinct from "what to archive." The session API answers "what trades, on which account, sourced from whose data." Plugin version is recorded per session (vision plugin-skew mitigation).

## G. Scaling seam (in-process now → multi-node later)

- The only seams required: `IStrategyDispatch` (data in) and `IOrderRouter` (orders out). Both have in-process implementations now.
- **Multi-node later:** node 1 holds account A's connection (data + A orders) and publishes A's per-instrument streams to Valkey/Garnet Streams (per the no-Redis decision); node 2 holds account B's connection (orders only), subscribes to A's streams off the bus, runs strategy set Y, routes orders to B. No rewrite — the bus implementation slots behind the same two interfaces.
- **Single-session cap acknowledged:** scaling *collection* past one session's yield means more accounts / market-data sessions (more connections), not a bigger socket. Scaling *strategies* means more execution-plane consumers, which the dispatch seam already permits.

## H. Recovery & durability (ties to vision M3b)

Unchanged direction: per-session JSONL event logs are the replayable source of truth; boot-time recovery replays the existing 3-phase reconciliation against live exchange state, per order session; heartbeat endpoint + staleness watchdog + alerting (Telegram/webhook). The binary tick log is **archival, not recovery state** — recovery rebuilds session/order/fill state from the event log plus an exchange query, exactly as M3b specifies. Accumulator partial-bar state is persisted (CAS JSON next to the feed) so a mid-bar restart re-seeds correctly.

## I. Vision-doc changes this implies

1. **§2 (Data plane):** the relay is an append-only **framed** log — **binary for tick feeds, JSONL for low-rate events** — plus a binary tick-record spec paragraph (64 B header + 40 B fixed frame + heartbeat/boundary frames).
2. **§2 / Q7:** add the **data-plane / execution-plane decomposition** (instrument-keyed dispatch vs account-keyed routing) as the LiveHost-internal model; state that data account and order account are independent axes.
3. **§1 LiveHost row:** note multi-account (data session vs order session, B pays no data lines) and the `IStrategyDispatch` / `IOrderRouter` seams.

## J. Phasing (maps onto the vision roadmap)

This is not a new milestone — it refines the **internals** of:
- **M3 (LiveHost extraction + durability):** the data-plane/execution-plane split, bounded channels, `ITickRouter` / `IStrategyDispatch` / `IOrderRouter` seams, tick entry point, account-scoped order routing, binary tick relay + JSONL events.
- **M6 (live alt-bars):** shared `IBarAccumulator`-driven live bars, seeded from history, with the golden test (incremental ≡ batch ≡ live).

**Prerequisite for the data plane (2026-06-21 addendum, see §B):** before Plan 4 wires the live driver, extract the alt-bar accumulator engine out of `HistoryLoader.Application` into a shared library so both the batch and live drivers use one implementation. The bar-source resolver (§B) and the threshold-freeze + partial-bar-seed parity guards (§B) land with the M3/M6 work respectively. Venue-published bars (§A½) are a cheap open/closed relay drop-in whenever a venue that publishes them comes online.

A standalone POC of the GC-free ingest→binary-archival path (synthetic 1000-instrument firehose, allocation + latency measured via the BenchmarkDotNet harness) is worth front-running before M3 wiring, mirroring how the IB-connector POC de-risked the broker path.

## K. Risks / open points

1. **Bounded-channel drop policy under sustained archival backpressure** — block ingest (risks the socket read loop) vs spill to a larger disk buffer. Leaning **spill-to-disk, then block**; threshold to be decided with a measured firehose.
2. **IB market-data line economics** for ~1000 instruments (snapshot cadence, booster packs) — a sizing exercise that bounds what "lossless" can even mean (you cannot lose what IB throttles, but must not drop what it sends).
3. **Accumulator seeding correctness across a mid-bar restart** — partial-bar state persistence shares the M6 golden-test burden.
4. **Connector-data extraction touches the working Binance path** — `ITickRouter` must be introduced without regressing the concurrent-venue model; covered by existing live tests + the M6 parity test.

## Verification

This is an architecture/design document. It is "done" when the owner signs off on the four planes, the data/execution split, the relay format split, the config model, the scaling seams, and the M3/M6 mapping. Implementation verification lands in the per-milestone speckit decomposition: GC/allocation measured via the BenchmarkDotNet harness on a synthetic firehose; losslessness via a relay→canonicalize round-trip with injected backpressure; multi-account isolation via a two-account routing test (set X→A, set Y→B, shared A data); parity via the M6 golden test.
