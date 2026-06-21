# HistoryLoader Binary-Tick Canonicalizer — Design

**Date:** 2026-06-21
**Status:** Design approved (brainstorming) — ready for implementation plan
**Scope:** Plan 2 of the LiveHost relay decomposition. HistoryLoader tails the Plan-1 binary `.atft` relay streams (`trades`, `quotes`, `_session`) off `IFileStorage` and canonicalizes them into its existing decimal, daily-partitioned CSV feeds — incrementally, idempotently, and as the **sole canonicalizer**. Completes the capture → archive → backtest round-trip.

## Context

Plan 1 (merged to `main`, PR #34 / squashed `5b9ef6c`) built `AlgoTradeForge.Live.Relay`: a vertically-sliced, stream-per-type binary relay. A `RelayWriter` deposits immutable, rotated `.atft` segments locally; a `SegmentUploader` sweeps completed segments to `IFileStorage` under a LiveHost-owned `live-md/{venue}/…` prefix. Each segment is a 64-byte `SegmentHeader` (carrying `PriceScaleExp`/`QtyScaleExp`/`FirstSequence`/`PayloadSize`) followed by fixed-width `IFramePayload<T>` frames. `SegmentReader<T>` reads them back as statically-typed `T`.

The service-decomposition vision (`docs/service-decomposition-vision.md`, Q7) and the LiveHost collection+execution design (`docs/superpowers/specs/2026-06-20-livehost-collection-execution-design.md`, §E) fix the contract: **HistoryLoader is the sole canonicalizer**. It tails each relay stream with a persisted cursor under CAS (the M6 incremental pattern), writes canonical partitions, and owns `feeds.json`. **LiveHost publishes raw and never touches partitions or `feeds.json`.**

This document designs the consumer (HistoryLoader) side of that contract. The producer side already exists and is unchanged.

### What already exists (reused, not rebuilt)

- **Producer:** `SegmentReader<T>` / `SegmentHeader` (`AlgoTradeForge.Live.Relay`); canonical payloads `TradeTick` (33 B, `trades`), `QuoteTick` (48 B, `quotes`), `SessionEvent` (9 B, `_session`) in `AlgoTradeForge.Domain.History`.
- **Canonical sinks:** `ITickFeedWriter` → `DailyTickCsvWriter` (`ticks/<YYYY-MM-DD>.csv`, schema `ts,price,qty,is_buyer_maker,agg_id`, dedup by `agg_id`); `IBookTickerWriter` → `DailyBookTickerCsvWriter` (`book-ticker/<YYYY-MM-DD>.csv`, schema `ts,bid_price,bid_qty,ask_price,ask_qty,update_id`, dedup by `update_id`). Both derive from `BufferedPartitionWriter` (atomic buffer-then-PUT, watermark dedup, `RegisterPartitionWatermark`).
- **CAS + resume primitives:** `IFileStorage.ReadWithEtag` / `WriteIfMatch` (throws `ConcurrencyConflictException` on mismatch); `IPartitionTailIndex.GetLastLine`; the `ResumeFrom` → `RegisterPartitionWatermark` pattern on each writer.

## Decisions (this brainstorming)

1. **Scope = all three streams** — `trades` + `quotes` + `_session`. The consumer is built open/closed (below), so three streams is barely more code than one.
2. **`_session` canonicalizes to a per-venue daily CSV** — `{venue}/_session/<YYYY-MM-DD>.csv`, schema `ts,kind`, idempotent by a `ts` watermark. `SessionEvent.Sequence` is always 0, but heartbeats are emitted by a single `PeriodicTimer` in monotonic, append-only time order (`RelayWriter.HeartbeatLoop`), so `ts` is a valid non-decreasing watermark. *(The "session events stay JSONL" rule in the collection+execution design governs LiveHost's separate order/fill **audit log** — a variable-shape recovery artifact, out of scope here. The relay `_session` stream became homogeneous binary in the stream-per-type redesign, so canonicalizing it uniformly to CSV is consistent with the newer model.)*
3. **Run shape = library + `BackgroundService` collector now** — the canonicalizer core is a tested library; a config-gated `BackgroundService` in `HistoryLoader.WebApi` tails the uploaded `live-md/` prefix (cursor + CAS). It is idle until Plan 3's LiveHost produces real segments, and fully exercised now against synthetic `.atft`.

## Out of scope

- The relay/producer side (Plan 1, unchanged).
- LiveHost host extraction and a real `IVenueConnector` (Plan 3).
- LiveHost's order/fill JSONL audit log and recovery replay (M3b).
- The cross-type `(TimestampMs, Sequence)` k-way merge — tail-cursor canonicalization is per-stream and never needs it.
- Gap *classification* logic (correlating data-stream gaps against `_session`) as a live alerting consumer — Plan 2 materializes the `_session` feed that such a consumer would read; wiring the consumer is Plan 3+.

## Architecture — the consumer-side seam

The one real architectural choice is the shape of the per-type seam. Chosen approach mirrors the producer's `IFramePayload<T>` open/closed design.

| | Approach | Open/closed | Verdict |
|---|---|---|---|
| **A** | Generic `StreamCanonicalizer<T>` + `IStreamProjection<T>` per type, registered by `T.StreamName` | New stream type = new projection file + 1 registry line; zero edits to the tail loop, cursor, or path model | **Chosen** |
| B | One canonicalizer with `switch (streamName)` | Reintroduces the discriminator Plan 1 deleted | Rejected |
| C | Reuse the producer's `IFrameCodec.FormatFrame` text and re-parse | Lossy, indirect, double-format | Rejected |

**Polymorphism rule (carried from Plan 1):** compile-time generics on the per-frame path (`SegmentReader<T>`, `IStreamProjection<T>` — zero boxing); runtime polymorphism only at the seam (projection registry by stream name, resolved once per segment, not per frame).

### The uniform projection shape

With Approach A, all three streams collapse to one mapping — decode a frame, build a `FeedRecord`, hand it to the stream's canonical writer:

```
(T frame, SegmentHeader header, SegmentLocation loc)  →  writer.Write(assetOrVenueDir, FeedRecord)
```

The projection **holds its own target writer** and performs the write, so the generic canonicalizer stays writer-agnostic — it decodes frames and calls `Apply`, nothing more. The existing writers already take a `FeedRecord` and derive their dedup key internally (`Values[3]` for ticks, `Values[4]` for book-ticker), so no separate dedup key is threaded through the seam.

```csharp
public interface IStreamProjection<T> where T : IFramePayload<T>
{
    string StreamName { get; }   // == T.StreamName; the registry key
    // Maps one decoded frame (+ its segment header, for scale exps) and writes it
    // to this projection's canonical sink. Resolves the target dir from loc.
    void Apply(in T frame, in SegmentHeader header, SegmentLocation loc);
}
```

This keeps the existing `ITickFeedWriter` / `IBookTickerWriter` untouched (no forced refactor): the `TradeTick` projection owns an `ITickFeedWriter`, the `QuoteTick` projection owns an `IBookTickerWriter`, the `SessionEvent` projection owns the new `ISessionFeedWriter`. All flow through the **existing** `BufferedPartitionWriter` machinery; the canonicalizer invents no new persistence mechanism — it is a typed tail loop + three thin projections + a cursor.

## Components

### 1. Segment path model

Parses an `.atft` storage key into its parts and classifies the stream:

```
live-md/{venue}/{instrument}/trades/{createdAtMs:D13}-{firstSeq:D19}.atft
live-md/{venue}/{instrument}/quotes/…
live-md/{venue}/{venue}/_session/…          ← venue occupies the instrument slot
```

`SegmentLocation { Venue, InstrumentOrVenue, StreamName, CreatedAtMs, FirstSequence, Key }`. Data streams (`trades`/`quotes`) resolve `InstrumentOrVenue` as an instrument; `_session` resolves it as the venue. The `D13`/`D19` zero-padding makes lexical key order identical to chronological order — the basis for the cursor compare.

### 2. Two-layer incremental tail (M6 cursor + CAS)

**Cursor layer (bounds the scan).** A per-`(venue, instrument, stream)` cursor stores the **last fully-consumed segment key**, persisted under a **HistoryLoader-owned** prefix — *not* inside LiveHost's `live-md/` raw prefix (preserves "LiveHost owns raw, HistoryLoader owns canonical state"). Proposed location: `_canon-cursors/{venue}/{instrumentOrVenue}/{stream}.cursor` on the same `IFileStorage`. Written via `WriteIfMatch` (CAS) with the etag read alongside it. Each cycle:

1. `ListKeys("live-md/{venue}/{instrument}/{stream}/", suffix: ".atft")`.
2. Filter to keys lexically greater than the cursor.
3. Process each whole segment in order (`SegmentReader<T>` → project → `writer.Write`).
4. Flush the writer (atomic PUT), then advance the cursor under CAS.

Segments are **immutable and complete** (`SegmentUploader` only sweeps rotated files, marked `.uploaded`), so segment-granularity is sufficient — there is no torn-final-frame case at the storage layer. CAS on the cursor makes a second canonicalizer instance safe (defense-in-depth on sole-canonicalizer; a `ConcurrencyConflictException` means another worker advanced it — re-read and continue).

**Watermark layer (idempotency at the boundary).** The existing writer dedup (`agg_id` / `update_id` / `ts` watermark, seeded by `ResumeFrom` + `RegisterPartitionWatermark`). If a crash lands between "rows flushed" and "cursor advanced," the boundary segment is reprocessed on restart; the watermark drops every already-written row, so the result is a clean no-op. The two layers are distinct concerns: the cursor bounds *how much we scan*; the watermark guarantees *what we write is duplicate-free*.

### 3. Three projections (the open/closed surface)

| Stream | Payload | Target writer | `FeedRecord.Values` | Dedup key |
|---|---|---|---|---|
| `trades` | `TradeTick` | `ITickFeedWriter` | `[unscale(Price), unscale(Qty), Aggressor==Sell ? 1 : 0, Sequence]` | `Sequence` (= `agg_id`) |
| `quotes` | `QuoteTick` | `IBookTickerWriter` | `[unscale(BidPrice), unscale(BidSize), unscale(AskPrice), unscale(AskSize), Sequence]` | `Sequence` (= `update_id`) |
| `_session` | `SessionEvent` | `ISessionFeedWriter` (new) | `[(double)Kind]` | `TimestampMs` |

**Un-scale** reads the scale exponents from the **segment header**, retiring Plan-1's hardcoded `(2, 0)` debt: `decimal = scaledLong / 10^exp`, mirroring `ScaleContext` semantics exactly so the round-trip is lossless. The exps live in `SegmentHeader.PriceScaleExp` / `QtyScaleExp`; `_session` registers `(0, 0)` and carries no scaled fields.

**Aggressor mapping:** canonical `is_buyer_maker` is `0 = buy-aggressive, 1 = sell-aggressive` (per `PartitionedSourceReader`). So `AggressorSide.Buy → 0`, `AggressorSide.Sell → 1`. `AggressorSide.Unknown` (equities default; not produced by the crypto path) maps to `0` with a note — it is not exercised in the crypto round-trip and is revisited when an equities venue lands.

### 4. The `_session` sink (new)

A small `BufferedPartitionWriter`-derived writer, structurally identical to `DailyTickCsvWriter`:

- Path `{venueDir}/_session/<YYYY-MM-DD>.csv`, schema `ts,kind`.
- Dedup watermark = `ts` (monotonic per the heartbeat loop).
- `ResumeFrom` mirrors the tick writer: read the latest partition's last line, seed the watermark.

`SessionEventKind` is written as its integer value; a `dump`-style reader can render names. Reuses `BufferedPartitionWriter`'s atomic publish — no new persistence code.

### 5. `TickCanonicalizerService` (BackgroundService)

Config-gated (`Canonicalizer:Enabled`, default off until Plan 3). On each tick interval it enumerates `(instrument, stream)` pairs under the configured `live-md/{venue}/` prefix and runs the tail loop for each. One `IFileStorage`, one venue prefix per configured canonicalizer. Honors the BG-service catch-filter convention (`IsTrueShutdown(ex, ct)` — never `catch when (ex is not OperationCanceledException)`; HttpClient/storage timeouts must not crash the host).

### Instrument → asset-dir mapping

The relay knows an **instrument string** (`"BTCUSDT"`); the canonical layout needs an **asset dir** (`binance/BTCUSDT_perp`). The collector config carries a `venue → { instrument → assetDir }` map (mirroring `AssetDirectoryName.From`, which adds `_perp` for perpetuals), defaulting to `{venue}/{instrument}` when unmapped. Synthetic tests supply an explicit map. This keeps venue-specific naming (perp suffix, symbol casing) in config, out of the canonicalizer core.

## GC / performance

Off the live hot path (this is archival post-processing, not ingest), so the GC bar is the existing collector bar, not the relay's zero-alloc bar. `SegmentReader<T>` already decodes frames over a pooled buffer with no per-frame boxing (generics). The buffered writers batch rows per partition. No new benchmark is required unless profiling shows the tail loop dominates a collection cycle; if it ever does, it goes through the BenchmarkDotNet harness, not ad-hoc asserts.

## Verification

- **Round-trip per type:** synthetic `SegmentWriter<T>` → `IFileStorage` → canonicalize → CSV equals expected decimals. Covers `TradeTick`, `QuoteTick`, `SessionEvent`.
- **Incremental ≡ batch (M6 golden):** canonicalize in one pass vs N passes with a simulated mid-stream crash (cursor not advanced) → byte-identical partitions.
- **Idempotency:** re-run over already-consumed segments → zero new rows (watermark holds); cursor CAS conflict re-reads cleanly.
- **Un-scale correctness:** a `TradeTick` written with header exps `(p, q)` reconstructs the exact decimal the relay scaled from.
- **Open/closed acceptance test:** a throwaway `DepthTick` projection canonicalizes end-to-end with **zero edits** to the tail loop, cursor, or path model — only a new projection file + 1 registry line. (Mirrors Plan 1's open/closed proof.)
- **End-to-end:** synthetic `.atft` → canonical `ticks/` → an existing backtest loader reads it back, closing capture → archive → backtest.

## Implementation plan preview

Roughly 7–8 dependency-ordered tasks (final shape set by the writing-plans pass):

1. Segment path model + `SegmentLocation` + tests.
2. `IStreamProjection<T>` seam + registry (by `T.StreamName`).
3. Three projections + un-scale helper (header-driven) + aggressor mapping.
4. `ISessionFeedWriter` + `DailySessionCsvWriter`.
5. Cursor + CAS tail loop (`StreamCanonicalizer<T>`) — **opus; concurrency/idempotency-critical**.
6. Canonicalizer wiring (resolve projection + writer + cursor per stream).
7. `TickCanonicalizerService` BackgroundService + DI + config + instrument→assetDir map.
8. Open/closed acceptance test (`DepthTick`) + end-to-end round-trip test.

Per-task review (opus for the concurrency-critical task, sonnet elsewhere); final whole-branch opus review plus the open/closed acceptance test, mirroring Plan 1's discipline.
