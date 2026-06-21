# LiveHost Relay — Stream-Per-Type Vertical-Slicing Refactor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor `AlgoTradeForge.Live.Relay` from the single interleaved, `FrameType`-discriminated stream (checkpoint `f3fd6ce`) into the open/closed, stream-per-type model in the design doc: each event type is a self-contained slice written to its own homogeneous stream, read by a generic typed reader, with venues as `IVenueConnector` slices and liveness in a per-venue `_session` stream.

**Design (authoritative):** `docs/superpowers/specs/2026-06-20-livehost-relay-vertical-slices-design.md`. Read it first — this plan implements it and does not restate its rationale.

**Architecture:** A new generic core — `IFramePayload<TSelf>` (C# 11 static-abstract members) + `SegmentWriter<T>`/`SegmentReader<T>` + a generic `StreamPipeline<T>` (the checkpoint's `TickRelayWriter`, generalized) — replaces the per-frame `switch`/`enum`/`RelayFrame` machinery. `RelayWriter` coordinates one pipeline per event type (`TradeTick`, `QuoteTick`, `SessionEvent`). Compile-time generics on the per-frame hot path (zero boxing); runtime polymorphism only at the coarse seams (`IVenueConnector`, the `dump-ticks` codec registry).

**Tech Stack:** C# 14 / .NET 10; `System.Buffers.Binary.BinaryPrimitives`; `System.Threading.Channels` (bounded); xUnit v3; existing `IFileStorage`; BenchmarkDotNet.

## Global Constraints

- **Target `net10.0`, `LangVersion 14`, `Nullable enable`, `ImplicitUsings enable`.** All binary little-endian via `BinaryPrimitives`.
- **GC-free per-frame path:** generic `SegmentWriter<T>` reuses a pooled `byte[T.Size]`; `T.WriteTo`/`T.ReadFrom` over spans; no boxing of `T`; no per-frame allocation. Runtime polymorphism (interfaces/virtual) ONLY at `IVenueConnector` and the codec registry — never per frame.
- **One dotnet process at a time.** Never run build/test in parallel.
- **Async I/O convention:** I/O-fronting methods are `Task`/`ValueTask` + `CancellationToken ct = default`, **no `Async` suffix**; no sync-over-async.
- **Comment convention:** prefer none; terse single-line only for a non-obvious layout/algorithm/pitfall; no identifier-restating XML; no `// path` header comments.
- **One public type per file**, named after the type.
- **Reuse, don't reinvent:** the 64-byte segment header codec, `AggressorSide`, the bounded-channel + single-drain + rotation + fsync model, copy-on-write volatile instrument publication, `LocalFileSegmentSink`, `SegmentUploader`, and the async `BeginSegment` seam all exist at `f3fd6ce` — generalize them, don't rewrite from scratch.
- **Regression net:** the 12 existing relay tests + the `TradeTick` domain tests must stay green (adapted to new APIs); add the new per-type tests below. After the refactor, the obsolete-type deletions (Task 14) remove tests that tested removed types — replace, don't orphan.
- **Leave edits unstaged** unless the owner says otherwise (owner manages commits/resets for review).

## File Structure

```
src/AlgoTradeForge.Domain/History/
  TradeTick.cs            # gains : IFramePayload<TradeTick>
  QuoteTick.cs            # NEW : IFramePayload<QuoteTick>
  AggressorSide.cs        # unchanged
src/AlgoTradeForge.Live.Relay/
  IFramePayload.cs        # NEW generic contract
  SessionEventKind.cs     # NEW enum (Heartbeat|SessionStart|SessionEnd|ConnectorRestart)
  SessionEvent.cs         # NEW : IFramePayload<SessionEvent>
  SegmentHeader.cs        # renamed from TickSegmentHeader (generalized; keep scale exponents)
  SegmentWriter.cs        # NEW generic SegmentWriter<T>  (replaces TickSegmentWriter)
  SegmentReader.cs        # NEW generic SegmentReader<T>  (replaces TickSegmentReader)
  ISegmentSink.cs         # renamed/generalized ITickSegmentSink (per-stream path)
  LocalFileSegmentSink.cs # generalized to {root}/{instrument}/{stream}/...
  StreamPipelineOptions.cs# renamed from TickRelayOptions
  StreamPipeline.cs       # NEW generic (generalized TickRelayWriter<T>)
  RelayWriter.cs          # NEW coordinator over the typed pipelines
  IFrameCodec.cs          # NEW registry descriptor (inspection seam)
  FrameCodecRegistry.cs   # NEW StreamName -> IFrameCodec
  SegmentUploader.cs      # adjust path->key for the stream component
  IVenueConnector.cs      # NEW venue slice seam (+ MarketDataSessionPolicy, IMarketEvent)
  # DELETED: FrameType.cs, RelayFrame.cs, RelayFrameFormatter.cs,
  #          SessionBoundaryReason.cs, TickSegmentWriter.cs, TickSegmentReader.cs,
  #          TickRelayWriter.cs, RelayFormat.cs (folded into SegmentHeader/constants)
src/AlgoTradeForge.DumpTicks/Program.cs   # codec-registry driven; reads any stream
tests/AlgoTradeForge.Live.Relay.Tests/    # per-type round-trip, multi-stream pipeline, uploader, dump
benchmarks/.../TickRelayBenchmarks.cs     # trades + quotes firehose
```

---

### Task 1: `IFramePayload<TSelf>` contract

**Files:** Create `src/AlgoTradeForge.Live.Relay/IFramePayload.cs`. Test: compile-only (exercised by Task 2+).

**Interfaces — Produces:**
```csharp
namespace AlgoTradeForge.Live.Relay;

public interface IFramePayload<TSelf> where TSelf : IFramePayload<TSelf>
{
    static abstract string StreamName { get; }          // on-disk stream folder: "trades" | "quotes" | "_session"
    static abstract int    PayloadSize { get; }         // fixed bytes per frame for this type
    long TimestampMs { get; }                            // for liveness/merge; every payload has one
    long Sequence { get; }                               // monotonic per (instrument, stream); markers may use 0
    int  WriteTo(Span<byte> dest);                        // serialize; returns bytes written (== PayloadSize)
    static abstract TSelf ReadFrom(ReadOnlySpan<byte> src);
    string Format();                                      // one human-readable line for dump-ticks
}
```

- [ ] **Step 1:** Write `IFramePayload.cs` exactly as above.
- [ ] **Step 2:** `dotnet build src/AlgoTradeForge.Live.Relay/` — expect existing build still green (interface unused yet).
- [ ] **Step 3:** Commit (if commits authorized): `feat(relay): IFramePayload<T> contract for vertical slices`.

---

### Task 2: `TradeTick` implements `IFramePayload<TradeTick>`

**Files:** Modify `src/AlgoTradeForge.Domain/History/TradeTick.cs`. Test: `tests/AlgoTradeForge.Live.Relay.Tests/TradeTickCodecTests.cs`.

> `TradeTick` lives in Domain; `IFramePayload` lives in Live.Relay (which references Domain). A Domain type cannot implement a Live.Relay interface without an inverted dependency. **Resolution:** move `IFramePayload<TSelf>` into `AlgoTradeForge.Domain/History/` (Domain) — it is a pure serialization contract with no Relay dependency, and Domain is the right home for the canonical types + their contract. Update Task 1's path to `src/AlgoTradeForge.Domain/History/IFramePayload.cs` and namespace `AlgoTradeForge.Domain.History`. (Relay references Domain, so all relay generics see it.)

**Interfaces — Produces:** `TradeTick : IFramePayload<TradeTick>` with `StreamName => "trades"`, `PayloadSize => 33` (ts8+price8+qty8+seq8+aggressor1), `WriteTo`/`ReadFrom` little-endian in that field order, `Format()` → `"TRADE ts=… price=… qty=… seq=… aggressor=…"`.

- [ ] **Step 1: Failing test** `TradeTickCodecTests.RoundTrips`:
```csharp
using AlgoTradeForge.Domain.History;
namespace AlgoTradeForge.Live.Relay.Tests;
public class TradeTickCodecTests
{
    [Fact]
    public void RoundTrips()
    {
        var t = new TradeTick(1_700_000_000_001, 5_000_000, 10, 7, AggressorSide.Sell);
        Span<byte> buf = stackalloc byte[TradeTick.PayloadSize];
        Assert.Equal(TradeTick.PayloadSize, t.WriteTo(buf));
        Assert.Equal(t, TradeTick.ReadFrom(buf));
    }
    [Fact]
    public void Format_IncludesAggressor()
        => Assert.Contains("aggressor=Sell",
            new TradeTick(1, 2, 3, 4, AggressorSide.Sell).Format());
}
```
- [ ] **Step 2:** Run `--filter FullyQualifiedName~TradeTickCodecTests` → FAIL (members don't exist).
- [ ] **Step 3:** Implement: add `: IFramePayload<TradeTick>` and the members. `WriteTo` writes ts/price/qty/seq as `Int64LittleEndian` at offsets 0/8/16/24 and `(byte)Aggressor` at 32; `ReadFrom` mirrors; `TimestampMs`/`Sequence` already exist as record properties.
- [ ] **Step 4:** Run → PASS (2).
- [ ] **Step 5:** Commit `feat(relay): TradeTick as IFramePayload slice`.

---

### Task 3: `QuoteTick` slice

**Files:** Create `src/AlgoTradeForge.Domain/History/QuoteTick.cs`. Test: `tests/AlgoTradeForge.Live.Relay.Tests/QuoteTickCodecTests.cs`.

**Interfaces — Produces:**
```csharp
public readonly record struct QuoteTick(
    long TimestampMs, long BidPrice, long BidSize, long AskPrice, long AskSize, long Sequence)
    : IFramePayload<QuoteTick>
{
    public static string StreamName => "quotes";
    public static int PayloadSize => 48;   // 6 × Int64
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
    // WriteTo/ReadFrom: ts,bid,bidSz,ask,askSz,seq at offsets 0,8,16,24,32,40
    // Format() => "QUOTE ts=… bid=…@… ask=…@… seq=…"
}
```

- [ ] **Step 1:** Failing test (round-trip + Format contains `bid=`/`ask=`).
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(relay): QuoteTick (BBO) slice`.

---

### Task 4: `SessionEvent` slice (liveness)

**Files:** Create `src/AlgoTradeForge.Live.Relay/SessionEventKind.cs`, `src/AlgoTradeForge.Domain/History/SessionEvent.cs`. Test: `SessionEventCodecTests.cs`.

> `SessionEventKind` may live in Relay or Domain; put it in Domain beside `SessionEvent` for cohesion (one slice). Delete the old `SessionBoundaryReason` in Task 14 (its values fold into `SessionEventKind`).

**Interfaces — Produces:**
```csharp
public enum SessionEventKind : byte { Heartbeat = 0, SessionStart = 1, SessionEnd = 2, ConnectorRestart = 3 }

public readonly record struct SessionEvent(long TimestampMs, SessionEventKind Kind)
    : IFramePayload<SessionEvent>
{
    public static string StreamName => "_session";
    public static int PayloadSize => 9;   // ts8 + kind1
    public long Sequence => 0;            // liveness frames are not sequence-keyed
    // WriteTo/ReadFrom: ts@0, (byte)Kind@8 ; Format() => "SESSION ts=… kind=…"
}
```

- [ ] **Step 1:** Failing round-trip test across all four `Kind` values + Format.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(relay): SessionEvent liveness slice`.

---

### Task 5: Generalize the segment header → `SegmentHeader`

**Files:** Rename `TickSegmentHeader.cs` → `SegmentHeader.cs` (type `SegmentHeader`); fold `RelayFormat`'s `HeaderSize`/`Magic`/`CurrentVersion` constants in as `SegmentHeader` consts (drop `FrameSize` — frame size is now `T.PayloadSize`, per stream). Test: update `TickSegmentHeaderTests.cs` → `SegmentHeaderTests.cs`.

**Interfaces — Produces:** `readonly record struct SegmentHeader(sbyte PriceScaleExp, sbyte QtyScaleExp, long EpochBaseMs, long CreatedAtMs, long FirstSequence)` with `const int Size = 64`, `static ReadOnlySpan<byte> Magic => "ATFT"u8`, `const ushort Version = 1`, `WriteTo`/`ReadFrom`. Header no longer stores a frame size (validated per-reader against `T.PayloadSize`); keep a 2-byte `PayloadSize` field in the header written from `T.PayloadSize` so a reader can sanity-check it matches `T` (throw `InvalidDataException` on mismatch). Scale exponents stay (0 for `_session`).

- [ ] **Step 1:** Update tests to `SegmentHeader` + the `PayloadSize` field round-trip + a mismatch-throws test.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Rename/implement; delete `RelayFormat.cs` (constants moved). Fix references.
- [ ] **Step 4:** `dotnet build src/AlgoTradeForge.Live.Relay/` may fail where old types reference `RelayFormat`/`TickSegmentHeader` — that's expected; those callers are replaced in Tasks 6–10. Run `SegmentHeaderTests` → PASS.
- [ ] **Step 5:** Commit `refactor(relay): generalize SegmentHeader, fold RelayFormat constants`.

---

### Task 6: Generic `SegmentWriter<T>`

**Files:** Create `src/AlgoTradeForge.Live.Relay/SegmentWriter.cs` (delete `TickSegmentWriter.cs` in Task 14). Test: covered by Task 7 round-trip.

**Interfaces — Produces:**
```csharp
public sealed class SegmentWriter<T> : IDisposable where T : IFramePayload<T>
{
    public SegmentWriter(Stream dest, in SegmentHeader header, bool leaveOpen = false); // writes header
    public void Write(in T payload);     // reuses a byte[T.PayloadSize]; payload.WriteTo
    public void Flush(bool toDisk);
    public void Dispose();
}
```
Mirror the checkpoint `TickSegmentWriter` exactly, but: one generic `Write(in T)` replaces the three `WriteX` methods; the reusable buffer is `byte[T.PayloadSize]`; header `PayloadSize` field set from `T.PayloadSize`.

- [ ] **Step 1:** Implement (transcribe from `TickSegmentWriter`, generalized).
- [ ] **Step 2:** `dotnet build` the file (compile check; full lib build still red until Task 10).
- [ ] **Step 3:** Commit `feat(relay): generic SegmentWriter<T>`.

---

### Task 7: Generic `SegmentReader<T>` + per-type round-trips

**Files:** Create `SegmentReader.cs`. Test: `SegmentRoundTripTests.cs` (replaces `TickSegmentRoundTripTests.cs`).

**Interfaces — Produces:**
```csharp
public sealed class SegmentReader<T> : IDisposable where T : IFramePayload<T>
{
    public SegmentReader(Stream src, bool leaveOpen = false);  // reads+validates header; checks header.PayloadSize == T.PayloadSize
    public SegmentHeader Header { get; }
    public bool TryRead(out T payload);   // false at clean EOF; throws EndOfStreamException on torn frame
}
```
EOF/torn logic identical to the checkpoint reader (`ReadAtLeast(buf, T.PayloadSize, throwOnEndOfStream:false)`: 0→false, partial→throw, full→`T.ReadFrom`).

- [ ] **Step 1: Failing tests** — `SegmentWriter<TradeTick>` → `SegmentReader<TradeTick>` round-trips a list of trades with full-struct equality; same for `QuoteTick`; same for `SessionEvent`; a torn-frame test; a `PayloadSize`-mismatch-throws test (write a `TradeTick` segment, read with `SegmentReader<QuoteTick>` → `InvalidDataException`).
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement `SegmentReader<T>`.
- [ ] **Step 4:** Run `SegmentRoundTripTests` → PASS.
- [ ] **Step 5:** Commit `feat(relay): generic SegmentReader<T> + per-type round-trips`.

---

### Task 8: Per-stream sink — `ISegmentSink` / `LocalFileSegmentSink`

**Files:** Rename `ITickSegmentSink.cs` → `ISegmentSink.cs`; update `LocalFileSegmentSink.cs`. Test: covered by Task 9/10.

**Interfaces — Produces:**
```csharp
public interface ISegmentSink
{
    ValueTask<Stream> BeginSegment(string streamName, string instrument, long firstSequence, long createdAtMs, CancellationToken ct = default);
    ValueTask CompleteSegment(string streamName, string instrument, Stream segment, CancellationToken ct = default);
}
```
`LocalFileSegmentSink` path becomes `{root}/{instrument}/{streamName}/{createdAtMs:D13}-{firstSequence:D19}.atft` (for `_session`, `instrument` is the venue id; `streamName` = "_session"). Keep the async `ValueTask` seam.

- [ ] **Step 1:** Rename + add `streamName` param threading through path construction.
- [ ] **Step 2:** Build the file.
- [ ] **Step 3:** Commit `refactor(relay): per-stream ISegmentSink`.

---

### Task 9: Generic `StreamPipeline<T>` (generalized `TickRelayWriter`)

**Files:** Rename `TickRelayOptions.cs` → `StreamPipelineOptions.cs`; create `StreamPipeline.cs` (delete `TickRelayWriter.cs` in Task 14). Test: `StreamPipelineTests.cs` (adapt `TickRelayWriterTests`).

**Interfaces — Produces:**
```csharp
public sealed class StreamPipeline<T> : IAsyncDisposable where T : IFramePayload<T>
{
    public StreamPipeline(ISegmentSink sink, StreamPipelineOptions options, TimeProvider time);
    public int RegisterInstrument(string instrument, sbyte priceScaleExp, sbyte qtyScaleExp);
    public bool TryEnqueue(int instrumentId, in T payload);
    public ValueTask Enqueue(int instrumentId, T payload, CancellationToken ct = default);
    public long DroppedCount { get; }
    public Task WaitForDrain();   // test support
}
```
Transcribe `TickRelayWriter` generalized to `T`: the drain calls `SegmentWriter<T>.Write`; `BeginSegment` is called with `T.StreamName`; rotation uses `T.PayloadSize`; copy-on-write volatile instrument array, drain-then-cancel dispose ordering, `CancellationToken.None` on close — all preserved. **Heartbeats are NOT a concern of the data pipelines** anymore (liveness moved to the `_session` pipeline driven by `RelayWriter` — Task 10); remove the per-pipeline heartbeat timer from this generic type. Segment-open keeps writing a `SessionStart`-equivalent? No — boundary/heartbeat semantics live only in the `_session` stream now; data-stream segments carry only data frames. (A data segment's existence + its header `CreatedAtMs` is enough; liveness is correlated against `_session`.)

- [ ] **Step 1: Failing tests** — adapt the three `TickRelayWriterTests` to `StreamPipeline<TradeTick>`: (a) all enqueued ticks persisted in order across rotation; (b) bounded-channel backpressure, `DroppedCount==0` via `Enqueue`; (c) a `StreamPipeline<QuoteTick>` persists quotes independently. Drop the heartbeat test here (moves to Task 10).
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run `StreamPipelineTests` → PASS.
- [ ] **Step 5:** Commit `feat(relay): generic StreamPipeline<T>`.

---

### Task 10: `RelayWriter` coordinator + `_session` heartbeat

**Files:** Create `RelayWriter.cs`. Test: `RelayWriterTests.cs`.

**Interfaces — Produces:**
```csharp
public sealed class RelayWriter : IAsyncDisposable
{
    public RelayWriter(string venue, ISegmentSink sink, StreamPipelineOptions options, TimeProvider time);
    public int RegisterInstrument(string instrument, sbyte priceScaleExp, sbyte qtyScaleExp); // registers across data pipelines
    public ValueTask WriteTrade(int instrumentId, TradeTick t, CancellationToken ct = default);
    public ValueTask WriteQuote(int instrumentId, QuoteTick q, CancellationToken ct = default);
    public ValueTask WriteSessionEvent(SessionEvent e, CancellationToken ct = default);       // → _session stream (instrument = venue)
}
```
Holds a `StreamPipeline<TradeTick>`, `StreamPipeline<QuoteTick>`, `StreamPipeline<SessionEvent>`. Owns the **heartbeat `PeriodicTimer(TimeProvider)`** (moved here from the data pipelines): on each tick, `WriteSessionEvent(new SessionEvent(now, Heartbeat))`. On start, emit `SessionStart`; on dispose, emit `SessionEnd` then drain all pipelines (drain-then-cancel ordering across all three).

- [ ] **Step 1: Failing tests** — (a) `WriteTrade`/`WriteQuote` land in `trades`/`quotes` streams under the instrument; (b) advancing `FakeTimeProvider` writes `Heartbeat` frames to `_session`; (c) dispose writes `SessionEnd` and flushes all streams; (d) `SessionStart` present at startup.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(relay): RelayWriter coordinator + _session heartbeat`.

---

### Task 11: Inspection registry + `dump-ticks` refactor

**Files:** Create `IFrameCodec.cs`, `FrameCodecRegistry.cs`; rewrite `src/AlgoTradeForge.DumpTicks/Program.cs`; delete `RelayFrameFormatter.cs` (Task 14). Test: `FrameCodecRegistryTests.cs`.

**Interfaces — Produces:**
```csharp
public interface IFrameCodec
{
    string StreamName { get; }
    int PayloadSize { get; }
    string FormatFrame(ReadOnlySpan<byte> payload);   // delegates to T.ReadFrom(...).Format()
}
public static class FrameCodecRegistry   // StreamName -> IFrameCodec
{
    public static IReadOnlyDictionary<string, IFrameCodec> Default { get; }  // trades, quotes, _session
    public static IFrameCodec For(string streamName);
}
```
Each codec is a tiny generic adapter `FrameCodec<T> : IFrameCodec where T : IFramePayload<T>` (`StreamName => T.StreamName`, `PayloadSize => T.PayloadSize`, `FormatFrame => T.ReadFrom(payload).Format()`). `dump-ticks <segment.atft>`: read `SegmentHeader`, infer stream from the parent folder name, `FrameCodecRegistry.For(folder)`, loop reading `PayloadSize` chunks → `FormatFrame`. No switch anywhere.

- [ ] **Step 1: Failing test** — registry returns a codec per stream name; `FormatFrame` of a serialized `TradeTick`/`QuoteTick`/`SessionEvent` contains the expected tokens; `For("unknown")` throws.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement registry + codecs; rewrite `Program.cs`.
- [ ] **Step 4:** Run → PASS; `dotnet build src/AlgoTradeForge.DumpTicks/` succeeds.
- [ ] **Step 5:** Commit `feat(relay): codec registry + registry-driven dump-ticks`.

---

### Task 12: `IVenueConnector` seam

**Files:** Create `IVenueConnector.cs`, `MarketDataSessionPolicy.cs`, `IMarketEvent.cs` (+ `MarketEvent` carriers as needed). Test: a no-op/fake connector test proving the contract.

**Interfaces — Produces:**
```csharp
public enum MarketDataSessionPolicy { Concurrent, SingleSession }
public interface IMarketEvent { long TimestampMs { get; } }   // carriers: TradeEvent(instrument,TradeTick), QuoteEvent(instrument,QuoteTick)
public interface IVenueConnector
{
    string Venue { get; }
    MarketDataSessionPolicy SessionPolicy { get; }
    IAsyncEnumerable<IMarketEvent> Stream(IReadOnlyList<InstrumentSubscription> subs, CancellationToken ct);
}
```
This is the seam only — concrete connectors (Binance/IB) are out of scope here. Provide a `FakeVenueConnector` in tests that yields a scripted sequence, and a small `RelayIngest` helper (or document the wiring) that pumps `IMarketEvent`s into `RelayWriter.WriteTrade/WriteQuote` by carrier type — this is the ONE place that maps event carrier → typed write, and it is a tiny dispatch at the venue/ingest seam (not the per-frame hot path).

- [ ] **Step 1: Failing test** — a `FakeVenueConnector` streaming a trade + a quote, pumped into a `RelayWriter`, produces a `trades` segment and a `quotes` segment with the expected frames.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement the seam + the ingest pump.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(relay): IVenueConnector seam + ingest pump`.

---

### Task 13: `SegmentUploader` multi-stream

**Files:** Modify `SegmentUploader.cs`. Test: update `SegmentUploaderTests.cs`.

The uploader already globs `*.atft` recursively; the only change is the key derivation — the local path is now `{root}/{instrument}/{stream}/{file}`, so the S3 key must be `{keyPrefix}/{instrument}/{stream}/{file}` (include the stream component). Verify trades + quotes + _session segments all upload to correctly-keyed objects and the marker idempotency still holds.

- [ ] **Step 1:** Update the failing test to assert keys include the stream segment (e.g. `live-md/ib/ESZ5/trades/…` and `…/quotes/…`).
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Fix key derivation (walk two path components: instrument + stream).
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `refactor(relay): uploader keys include stream component`.

---

### Task 14: Delete obsolete single-stream types

**Files:** Delete `FrameType.cs`, `RelayFrame.cs`, `RelayFrameFormatter.cs`, `SessionBoundaryReason.cs`, `TickSegmentWriter.cs`, `TickSegmentReader.cs`, `TickRelayWriter.cs`, `RelayFormat.cs` (if not already folded in Task 5). Delete/replace their orphaned tests (`RelayFormatTests.cs`, `TickSegmentHeaderTests.cs` → `SegmentHeaderTests.cs` done in Task 5, `RelayFrameFormatterTests.cs`, `TickSegmentRoundTripTests.cs` → done in Task 7, `TickRelayWriterTests.cs` → `StreamPipelineTests.cs` done in Task 9).

- [ ] **Step 1:** Delete the obsolete source files + any test files made redundant.
- [ ] **Step 2:** `dotnet build AlgoTradeForge.slnx` → succeeds (no dangling references).
- [ ] **Step 3:** `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/` → all green.
- [ ] **Step 4:** Commit `refactor(relay): remove single-stream interleaved-frame types`.

---

### Task 15: Trades + quotes firehose benchmark

**Files:** Modify `benchmarks/.../TickRelayBenchmarks.cs` (rename → `RelayBenchmarks.cs`).

Drive 1000 instruments × (N trades + N quotes) through a `RelayWriter` to a temp-dir sink. Two `[Benchmark]` methods (or one mixed) with `[MemoryDiagnoser]` + `[Config(typeof(BriefJsonConfig))]`. Headline = Allocated/op at the pooled-buffer floor (per-frame alloc ≈ 0).

- [ ] **Step 1:** Add the relay project ref if missing; write the benchmark against `RelayWriter`.
- [ ] **Step 2:** `dotnet build benchmarks/AlgoTradeForge.Benchmarks/ -c Release` → succeeds.
- [ ] **Step 3:** Optional smoke: `save-baseline.ps1 -Filter '*Relay*' -Job dry` (no competing dotnet); record Allocated. Non-blocking.
- [ ] **Step 4:** Commit `bench(relay): trades+quotes firehose on RelayWriter`.

---

## Final verification

- [ ] `dotnet build AlgoTradeForge.slnx` → clean.
- [ ] `dotnet test tests/AlgoTradeForge.Live.Relay.Tests/` → all green.
- [ ] `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter FullyQualifiedName~Tick` → green.
- [ ] **Open/closed proof:** introduce a throwaway `DepthTick : IFramePayload<DepthTick>` slice (StreamName "depth") + register its codec — confirm it round-trips and `dump-ticks` formats it with **zero edits** to `SegmentReader`/`SegmentWriter`/`StreamPipeline`/`RelayWriter`/`dump-ticks`. Then revert the throwaway. This is the architectural acceptance test.

## Self-Review (during planning)

- **Design coverage:** stream-per-type (Tasks 6–10), `IFramePayload<T>` slices (1–4), `_session` liveness (4, 10), generic readers/writers no-switch (6,7,9), codec-registry formatter no-switch (11), `IVenueConnector` slice (12), ordering carried by `TimestampMs`+`Sequence` on every payload (1), GC-free pooled buffers + no boxing (6,9, benchmark 15). Open/closed proof in Final verification.
- **Dependency fix recorded:** `IFramePayload` placed in Domain (Task 2 note) so Domain canonical types can implement it without an inverted reference — applied retroactively to Task 1.
- **Deletions tracked:** Task 14 removes exactly the types the new generic core replaces; redundant tests are replaced in their owning tasks, not orphaned.
- **Reuse honored:** header codec, bounded-channel/drain/rotation/fsync, COW instrument publication, sink, uploader, async BeginSegment all generalized from `f3fd6ce`, not rewritten.
