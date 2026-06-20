# LiveHost Relay — Vertically-Sliced, Stream-Per-Type Design

**Date:** 2026-06-20
**Status:** Design for approval (supersedes the single-stream relay of the Plan-1 checkpoint `f3fd6ce`)
**Scope:** Refactor `AlgoTradeForge.Live.Relay` from one interleaved, discriminated frame stream into an **open/closed, vertically-sliced** relay where a new venue or a new market-data event type is a self-contained slice you drop in — never an edit to a central `enum` or a fan of `switch` blocks.

## Why this supersedes the single-stream relay

The Plan-1 relay (checkpointed at `f3fd6ce`) put every event type — `Trade`, `Heartbeat`, `SessionBoundary` — into **one interleaved binary stream**, discriminated by a `FrameType` byte and dispatched by hardcoded `switch` blocks in the reader, the writer, and the formatter. Adding a venue's new data format (quotes, depth, greeks) meant editing the `FrameType` enum and every switch. That is closed for extension.

The roadmap is explicitly multi-venue and multi-format (crypto → IB → FX → options; trades, quotes/BBO, later depth/greeks). The relay must make **"support a new thing" = "add a new file," not "edit N existing files."**

## The organizing principle: one slice per type, polymorphism chosen by hot-path side

A **vertical slice** owns everything about one event type or one venue: its data shape, its binary layout, its formatting, its stream infrastructure (where its bytes live), and — for venues — its source-format mapping. Nothing about it leaks into a shared switch.

Two kinds of polymorphism, picked deliberately:

| Path | Frequency | Mechanism | Why |
|---|---|---|---|
| **Per-frame** (encode/decode each tick) | millions/sec | **Compile-time generics** + C# 11 static-abstract members | Fully typed, **zero boxing/allocation** — preserves the GC-free guarantee |
| **Coarse seams** (a venue, the dump/inspection registry) | per connection / per CLI run | **Runtime polymorphism** (interfaces, virtual dispatch) | Maximum flexibility; per-call cost is irrelevant off the hot path |

This is the rule the rest of the design follows.

## Stream-per-type: the move that removes the discriminator

The key structural change: **one homogeneous stream per event type**, instead of one interleaved stream.

```
live-md/{venue}/
  {instrument}/
    trades/   2026-06-20T14.atft     ← pure TradeTick frames, fixed width
    quotes/   2026-06-20T14.atft     ← pure QuoteTick frames, fixed width
  _session/   2026-06-20T14.atft     ← heartbeat + SessionStart/End/ConnectorRestart
```

`★` Because each stream is **homogeneous**, the type is encoded in the *path*, not in a byte inside each frame. The discriminator byte disappears, and with it every read-time `switch`: a `trades` stream is read by `SegmentReader<TradeTick>`, which returns `TradeTick` **statically**. Generics — impossible on a heterogeneous stream — now work, because there is exactly one `T` per stream. Fixed-width-per-stream also returns (all `TradeTick` are the same size), so random access within a stream is preserved without a length prefix.

This also matches the rest of AlgoTradeForge: **HistoryLoader is already feed-per-type** (klines, funding, OI, ratios, `bookTicker`, …), each its own folder + `feeds.json` entry, tailed independently. Stream-per-type generalizes a split Plan-1 already started (binary ticks vs JSONL events). The interleaved stream was the outlier.

## Liveness is per-producer, so it gets its own slice

Heartbeats and session boundaries do **not** belong in data streams. Liveness is a property of the *producer* (LiveHost), not of any one data stream — if LiveHost is up, all its streams are live; if it crashed, all are dead. So a single **`_session` stream per venue** carries heartbeat + `SessionStart`/`SessionEnd`/`ConnectorRestart`.

Gap interpretation becomes a correlation: *no `TradeTick` for instrument X in a window* **and** *`_session` shows the producer alive* ⇒ market was quiet; *`_session` gap* ⇒ producer was down. This keeps every data stream **100% homogeneous** and makes "session/liveness" just another slice (`SessionEvent`) with its own typed stream.

## The slices

### 1. Event-type slice — `IFramePayload<TSelf>`

Each event type is a `readonly record struct` that owns its binary layout and its text rendering, via static-abstract members (no instance vtable, no boxing):

```csharp
public interface IFramePayload<TSelf> where TSelf : IFramePayload<TSelf>
{
    static abstract string StreamName { get; }          // "trades", "quotes", "_session" — its on-disk home
    static abstract int    Size { get; }                // fixed payload width for this type
    int  WriteTo(Span<byte> dest);                       // serialize self → returns bytes written
    static abstract TSelf ReadFrom(ReadOnlySpan<byte> src);
    string Format();                                     // self-describing line for dump-ticks
}

public readonly record struct TradeTick(long TimestampMs, long Price, long Quantity, long Sequence, AggressorSide Aggressor)
    : IFramePayload<TradeTick> { public static string StreamName => "trades"; public static int Size => 33; /* … */ }

public readonly record struct QuoteTick(long TimestampMs, long BidPrice, long BidSize, long AskPrice, long AskSize, long Sequence)
    : IFramePayload<QuoteTick> { public static string StreamName => "quotes"; public static int Size => 48; /* … */ }

public readonly record struct SessionEvent(long TimestampMs, SessionEventKind Kind)   // Heartbeat | SessionStart | SessionEnd | ConnectorRestart
    : IFramePayload<SessionEvent> { public static string StreamName => "_session"; /* … */ }
```

Adding `DepthTick` or `GreekTick` later = add one struct. No other file changes.

### 2. Stream-infrastructure slice — generic `SegmentWriter<T>` / `SegmentReader<T>`

The segment file machinery (header, rotation, fsync, segment naming) becomes generic over the payload — *the stream infrastructure is part of each slice*, instantiated per type:

```csharp
public sealed class SegmentWriter<T> : IAsyncDisposable where T : IFramePayload<T> { void Write(in T payload); … }
public sealed class SegmentReader<T> where T : IFramePayload<T> { bool TryRead(out T payload); … }
```

The 64-byte segment header from the checkpoint is reused (it already records scale exponents + first sequence); the per-frame body is now `T.WriteTo`/`T.ReadFrom`. No type byte, no per-frame switch. The bounded-channel multi-instrument relay writer, rotation, fsync-on-rotation, and the `SegmentUploader` sweep are all reused — generalized from "the tick stream" to "a stream of `T`."

### 3. Venue slice — `IVenueConnector` (runtime-polymorphic seam)

A venue is a class implementing one interface; it owns its wire format and normalizes into canonical payloads:

```csharp
public interface IVenueConnector
{
    string Venue { get; }
    MarketDataSessionPolicy SessionPolicy { get; }      // Concurrent | SingleSession (Q7)
    IAsyncEnumerable<IMarketEvent> Stream(IReadOnlyList<InstrumentSubscription> subs, CancellationToken ct);
}
```

`BinanceConnector`, `IbConnector`, … are runtime-polymorphic (resolved once per connection, not per tick). Each emits canonical `TradeTick`/`QuoteTick` (+ sidecar metadata for venue-specific fields); the relay routes each event to its type's stream by `T.StreamName`. Adding a venue touches no relay code — it ties directly to the existing `connector.implement` skill.

### 4. Inspection slice — codec registry (runtime-polymorphic, off hot path)

`dump-ticks` and any generic tooling use a registry of lightweight descriptors so they need no knowledge of concrete types:

```csharp
public interface IFrameCodec { string StreamName { get; } string FormatFrame(ReadOnlySpan<byte> frame); }
// registry: StreamName → IFrameCodec; dump-ticks looks up by the stream folder it's reading.
```

This is where runtime polymorphism earns its keep: the formatter `switch` is gone — `dump-ticks` reads a stream, finds its codec by folder name, and calls `FormatFrame`. Adding a type registers a codec; the tool is never edited.

## Ordering

Per-stream order is preserved (each stream is append-only, time-ordered). **Cross-type** global order (was this trade before that quote at the same instant?) is no longer implicit — a consumer that needs it does a **k-way merge by `(TimestampMs, Sequence)`** across streams. Every canonical event carries `TimestampMs` + a monotonic `Sequence`, so the merge is deterministic and ties break by sequence. For tail-cursor consumers (HistoryLoader canonicalizing one feed) this never arises; it is only the merging consumer's concern.

## GC profile (unchanged guarantee)

Zero per-frame heap allocation on ingest/archival: `SegmentWriter<T>` reuses a pooled `byte[T.Size]`; `T.WriteTo`/`ReadFrom` operate over spans; generics mean no boxing of `T`; the bounded channel and copy-on-write instrument publication carry over from the checkpoint. Runtime polymorphism appears only at `IVenueConnector` (per event-batch) and the inspection registry (per CLI run) — never per frame.

## What carries over from the checkpoint (`f3fd6ce`)

Reused, generalized: the 64-byte `TickSegmentHeader` (binary codec), `AggressorSide`, `BinaryPrimitives` little-endian discipline, the bounded-channel + single-drain + rotation + fsync model, `LocalFileSegmentSink`, `SegmentUploader` (marker-idempotent sweep), the async `BeginSegment` seam. Removed: the `FrameType` enum, `RelayFrame`, `RelayFrameFormatter`'s switch, and the three `WriteTick/WriteHeartbeat/WriteSessionBoundary` methods (replaced by generic `Write<T>` + per-type `WriteTo`).

## Tradeoffs / risks

1. **File/object proliferation** — N instruments × M types + a `_session` stream. Quotes are far higher volume than trades; each stream rotates/retains/syncs independently (a feature), but handle/object counts rise. Mitigation: per-stream sharding + retention; revisit if S3 small-object counts bite.
2. **Cross-type ordering** is now a read-time merge (above) — acceptable for tail-cursor consumers; documented for any merging consumer.
3. **Format change** — this changes the on-disk layout vs the checkpoint. Cheap now (no persisted data); the checkpoint commit is the rollback point.
4. **Static-abstract-member generics** require C# 11+ (have .NET 10 / C# 14 — fine) and are less familiar; the design doc + clear examples mitigate.

## Verification

- Round-trip per type: `SegmentWriter<T>` → `SegmentReader<T>` returns equal `T` (covers `TradeTick` and `QuoteTick`).
- Homogeneity: a `trades` reader over a trades stream never needs a type byte; a `QuoteTick` written to the quotes stream round-trips independently.
- Liveness: a gap in a data stream is correctly classified against `_session` heartbeats (producer-down vs market-quiet).
- Multi-type firehose benchmark: 1000 instruments × (trades + quotes) through the generic writers — allocation stays at the pooled-buffer floor (no per-frame alloc).
- `bookTicker → QuoteTick` mapping produces a quotes stream a `SegmentReader<QuoteTick>` reads back exactly.
- "Add a type" smoke: introducing a throwaway `DepthTick` slice requires touching no existing relay/reader/writer/formatter file (the open/closed proof).

This document is the authoritative relay design; the implementation plan (replacing the superseded single-stream plan) follows on approval.
