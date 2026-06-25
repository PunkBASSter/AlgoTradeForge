# Live Reconnect / Catch-up Replay (Core + Binance) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the live alt-bar data plane to the present — at cold session start and after a (silent) Binance WS reconnect — by replaying archived source records through the existing aggregation engine, so the strategy gets warm history and the in-progress (partial) bar continues the historical series. No persisted accumulator state.

**Architecture:** A venue-agnostic recovery core in `LiveHost.Application/Live/Recovery/` (`ICatchupGate`/`SequenceWatermarkGate`, `IReplaySource`, `IBackfillRequester`, `CatchupCoordinator`, `Discontinuity`/`RecoveryPolicy`) plus a Binance impl in `LiveHost.Infrastructure`. `TickAggregationBarSource` becomes catch-up-aware: its `Start()` seeds warmup bars from the persisted alt-bar feed, then replays source records through a monotonic catch-up gate (sequence-based for Binance) into one accumulator, then drains buffered live ticks — a single ordered, self-deduping stream. Gaps (aggId discontinuity) trigger bounded-wait-then-declare (policy B). The IB impl and IB drop-signal wiring ride later into Plan 3/4 against these seams.

**Tech Stack:** C# 14 / .NET 10, `System.Threading.Channels`, xUnit + NSubstitute, `IFileStorage` (`AlgoTradeForge.Storage`), the relay `SegmentReader<TradeTick>` frame decode, `PartitionedCsvBarLoader` (`IInt64BarLoader`).

## Global Constraints

- **C# 14 / .NET 10.** ONE `dotnet` process at a time (build/test strictly sequential). Use `powershell.exe`, never `pwsh`.
- **Domain has ZERO ProjectReferences** and is venue-neutral. `IBarAccumulator` is **unchanged** — replay never inspects accumulator internals; "reset at a clean boundary" is a fresh `AccumulatorEntry.Open`.
- **LiveHost must NOT depend on `HistoryLoader.Application`.** The canonical read-side is the shared `AlgoTradeForge.Infrastructure` (`PartitionedCsvBarLoader`) + shared `AlgoTradeForge.Storage`. The relay `.atft` decode is `AlgoTradeForge.Live.Relay.SegmentReader<TradeTick>`.
- **No `Async` suffix** on new async methods. **using-over-try/finally.** **One type per file.**
- **Int64 money:** never raw `(long)` casts on monetary values; `ScaleContext` at boundaries. This plan moves existing scaled `long`s verbatim (ticks already scaled by the connector); no new scaling.
- **Gap signal = aggId contiguity** (`TradeTick.Sequence`), not time-gaps. A quiet market keeps aggIds contiguous; a disconnect jumps them.
- **Every channel bounded.** The live-tick buffer during catch-up is a bounded channel; the market-data path stays drop-newest (dropped live ticks are in the archive).
- Commits: implementer does NOT `git add`/commit (hook-denied); the controller stages + commits per task after review. Steps show the intended message; the controller runs it.

---

### Task 1: Recovery vocabulary — `Discontinuity`, `DiscontinuityReason`, `RecoveryPolicy`

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/DiscontinuityReason.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/Discontinuity.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/RecoveryPolicy.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/RecoveryVocabularyTests.cs`

**Interfaces:**
- Produces:
  - `enum DiscontinuityReason { Disconnect, MissingArchive }`
  - `readonly record struct Discontinuity(long FromTs, long ToTs, DiscontinuityReason Reason)` — time-based and venue-agnostic; aggId-based detection lives in `SequenceWatermarkGate` (behind `ICatchupGate`), not here.
  - `sealed record RecoveryPolicy(TimeSpan BackfillBudget, TimeSpan PollInterval) { static RecoveryPolicy NoBackfill { get; } }`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/RecoveryVocabularyTests.cs
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class RecoveryVocabularyTests
{
    [Fact]
    public void Discontinuity_carries_time_window_and_reason()
    {
        var d = new Discontinuity(FromTs: 1000, ToTs: 2000, DiscontinuityReason.Disconnect);
        Assert.Equal(1000, d.FromTs);
        Assert.Equal(2000, d.ToTs);
        Assert.Equal(DiscontinuityReason.Disconnect, d.Reason);
    }

    [Fact]
    public void NoBackfill_policy_has_zero_budget()
    {
        Assert.Equal(TimeSpan.Zero, RecoveryPolicy.NoBackfill.BackfillBudget);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter RecoveryVocabularyTests`
Expected: FAIL — types do not exist (compile error).

- [ ] **Step 3: Create the types**

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/DiscontinuityReason.cs
namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

public enum DiscontinuityReason
{
    /// <summary>Source records were lost (connection dropped) and could not be recovered.</summary>
    Disconnect,
    /// <summary>The archive had no records to bridge a detected aggId gap within budget.</summary>
    MissingArchive,
}
```

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/Discontinuity.cs
namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// A break in the source-record stream the catch-up could not bridge, as a time window. Venue-
/// agnostic: every venue has timestamps, and every consumer (backfill REST start/end, HistoryLoader
/// heal, FE marker) works on time ranges. The venue-specific detection signal (Binance aggId
/// discontinuity, IB connection events) stays inside the detector and never reaches this marker.
/// </summary>
public readonly record struct Discontinuity(long FromTs, long ToTs, DiscontinuityReason Reason);
```

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/RecoveryPolicy.cs
namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Per-venue gap policy. On a true gap the coordinator requests backfill and polls every
/// <see cref="PollInterval"/> up to <see cref="BackfillBudget"/>; budget zero == declare immediately.
/// </summary>
public sealed record RecoveryPolicy(TimeSpan BackfillBudget, TimeSpan PollInterval)
{
    public static RecoveryPolicy NoBackfill { get; } = new(TimeSpan.Zero, TimeSpan.Zero);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter RecoveryVocabularyTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/Recovery/ tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/RecoveryVocabularyTests.cs
git commit -F - <<'EOF'
feat(livehost): recovery vocabulary — Discontinuity + RecoveryPolicy
EOF
```

---

### Task 2: `ICatchupGate` + `SequenceWatermarkGate` — aggId dedupe + gap detection (sequence-venue impl)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/TickAdmission.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/ICatchupGate.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/SequenceWatermarkGate.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/SequenceWatermarkGateTests.cs`

**Interfaces:**
- Consumes: `TradeTick` (`AlgoTradeForge.Domain.History`).
- Produces:
  - `enum TickAdmission { Accept, Duplicate, Gap }` (venue-neutral).
  - `interface ICatchupGate` (venue-agnostic): `TickAdmission Admit(in TradeTick tick)`, `void Reseed(in TradeTick tick)`, `bool Seeded { get; }`, `long LastTimestampMs { get; }`. The ONLY surface `CatchupCoordinator` depends on — time-based, never sequence-based.
  - `sealed class SequenceWatermarkGate : ICatchupGate` — the sequence-venue base impl (Binance aggId + any future venue with monotonic per-instrument ids). Adds `long LastSequence { get; }` for its own use. IB (no contiguous tick sequence) wires a different `ICatchupGate` + connection-event trigger in Plan 3/4.
  - Semantics of `SequenceWatermarkGate`: first tick (unseeded) → `Accept`, seeds `LastSequence` + `LastTimestampMs`. `Sequence <= LastSequence` → `Duplicate`. `Sequence == LastSequence + 1` → `Accept`, advances both. `Sequence > LastSequence + 1` → `Gap` (does NOT advance). `Reseed(in tick)` forces both to the given tick (used after a declared discontinuity). `LastTimestampMs` lets the coordinator build the `Discontinuity` time window from the last-good tick.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/SequenceWatermarkGateTests.cs
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class SequenceWatermarkGateTests
{
    private static TradeTick Tick(long seq, long ts = 0) =>
        new(TimestampMs: ts, Price: 100, Quantity: 1, Sequence: seq, Aggressor: AggressorSide.Buy);

    [Fact]
    public void First_tick_is_accepted_and_seeds_watermark()
    {
        var gate = new SequenceWatermarkGate();
        Assert.Equal(TickAdmission.Accept, gate.Admit(Tick(5)));
        Assert.True(gate.Seeded);
        Assert.Equal(5, gate.LastSequence);
    }

    [Fact]
    public void Contiguous_tick_accepted_duplicate_dropped_gap_flagged()
    {
        var gate = new SequenceWatermarkGate();
        gate.Admit(Tick(5));
        Assert.Equal(TickAdmission.Accept, gate.Admit(Tick(6)));     // contiguous
        Assert.Equal(TickAdmission.Duplicate, gate.Admit(Tick(6))); // replay/live overlap
        Assert.Equal(TickAdmission.Duplicate, gate.Admit(Tick(4))); // older
        Assert.Equal(TickAdmission.Gap, gate.Admit(Tick(9)));       // jump
        Assert.Equal(6, gate.LastSequence);                              // gap did NOT advance
    }

    [Fact]
    public void Reseed_accepts_the_new_contiguous_run()
    {
        var gate = new SequenceWatermarkGate();
        gate.Admit(Tick(5));
        Assert.Equal(TickAdmission.Gap, gate.Admit(Tick(20)));
        gate.Reseed(Tick(20));                                           // discontinuity declared at 20
        Assert.Equal(TickAdmission.Accept, gate.Admit(Tick(21)));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter SequenceWatermarkGateTests`
Expected: FAIL — `ICatchupGate`/`SequenceWatermarkGate`/`TickAdmission` do not exist.

- [ ] **Step 3: Create the types**

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/TickAdmission.cs
namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

public enum TickAdmission { Accept, Duplicate, Gap }
```

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/ICatchupGate.cs
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Venue-agnostic catch-up gate: admits ticks, dedupes the replay→live overlap, and flags a gap
/// (the disconnect signal). Time-based — exposes only <see cref="LastTimestampMs"/>, never a
/// sequence — so the coordinator stays venue-neutral. HOW a gap is detected is the impl's concern
/// (sequence for crypto, connection events for IB).
/// </summary>
public interface ICatchupGate
{
    bool Seeded { get; }
    long LastTimestampMs { get; }
    TickAdmission Admit(in TradeTick tick);
    void Reseed(in TradeTick tick);
}
```

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/SequenceWatermarkGate.cs
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Sequence-venue <see cref="ICatchupGate"/>: monotonic dedupe + gap detection on venue aggId
/// (<see cref="TradeTick.Sequence"/>), reusable by any venue with contiguous per-instrument ids
/// (Binance today). Both replayed and live ticks pass through one instance so the replay→live
/// overlap self-dedupes; a non-contiguous jump is reported as <see cref="TickAdmission.Gap"/>.
/// Single-threaded: the owning bar source serializes admission on the processing path.
/// </summary>
public sealed class SequenceWatermarkGate : ICatchupGate
{
    private long _last;
    public bool Seeded { get; private set; }
    public long LastSequence => _last;
    public long LastTimestampMs { get; private set; }

    public TickAdmission Admit(in TradeTick tick)
    {
        if (!Seeded)
        {
            Seeded = true;
            _last = tick.Sequence;
            LastTimestampMs = tick.TimestampMs;
            return TickAdmission.Accept;
        }
        if (tick.Sequence <= _last) return TickAdmission.Duplicate;
        if (tick.Sequence == _last + 1)
        {
            _last = tick.Sequence;
            LastTimestampMs = tick.TimestampMs;
            return TickAdmission.Accept;
        }
        return TickAdmission.Gap;
    }

    public void Reseed(in TradeTick tick)
    {
        Seeded = true;
        _last = tick.Sequence;
        LastTimestampMs = tick.TimestampMs;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter SequenceWatermarkGateTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/Recovery/TickAdmission.cs src/AlgoTradeForge.LiveHost.Application/Live/Recovery/ICatchupGate.cs src/AlgoTradeForge.LiveHost.Application/Live/Recovery/SequenceWatermarkGate.cs tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/SequenceWatermarkGateTests.cs
git commit -F - <<'EOF'
feat(livehost): ICatchupGate + SequenceWatermarkGate — aggId dedupe + gap detection
EOF
```

---

### Task 3: `IReplaySource` + `ReplayRequest` + `IBackfillRequester` abstractions

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/ReplayRequest.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/IReplaySource.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/IBackfillRequester.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/ReplayAbstractionsTests.cs`

**Interfaces:**
- Consumes: `Asset` (`AlgoTradeForge.Domain`), `TradeTick`, `Discontinuity`, `RecoveryPolicy`.
- Produces:
  - `readonly record struct ReplayRequest(Asset Asset, string Venue, string SourceFeedId, long FromTs)`
  - `interface IReplaySource { IAsyncEnumerable<TradeTick> Replay(ReplayRequest request, CancellationToken ct = default); }`
  - `interface IBackfillRequester { Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, CancellationToken ct = default); }`
- The test defines reusable fakes (`FakeReplaySource`, `FakeBackfillRequester`) that Tasks 5/6 reuse.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/ReplayAbstractionsTests.cs
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Assets;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class ReplayAbstractionsTests
{
    internal static Asset Btc() => CryptoPerpetualAsset.Create("BTCUSDT", "binance", baseAsset: "BTC", quoteAsset: "USDT", tickSize: 0.01m, stepSize: 0.001m);

    [Fact]
    public async Task FakeReplaySource_yields_configured_ticks_in_order()
    {
        var ticks = new[] { Tick(10), Tick(11), Tick(12) };
        var src = new FakeReplaySource(ticks);
        var req = new ReplayRequest(Btc(), "binance", "ticks", FromTs: 0);

        var got = new List<long>();
        await foreach (var t in src.Replay(req)) got.Add(t.Sequence);

        Assert.Equal(new long[] { 10, 11, 12 }, got);
    }

    internal static TradeTick Tick(long seq, long ts = 0) =>
        new(ts, Price: 100, Quantity: 1, Sequence: seq, Aggressor: AggressorSide.Buy);
}

internal sealed class FakeReplaySource(IReadOnlyList<TradeTick> ticks) : IReplaySource
{
    public async IAsyncEnumerable<TradeTick> Replay(ReplayRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct = default)
    {
        foreach (var t in ticks) { ct.ThrowIfCancellationRequested(); yield return t; await Task.Yield(); }
    }
}

internal sealed class FakeBackfillRequester(bool closes) : IBackfillRequester
{
    public int Calls { get; private set; }
    public Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, System.Threading.CancellationToken ct = default)
    { Calls++; return Task.FromResult(closes); }
}
```

> NOTE: confirm the `CryptoPerpetualAsset.Create(...)` parameter shape against `src/AlgoTradeForge.Domain/Assets/CryptoPerpetualAsset.cs` and adjust the `Btc()` helper if the factory differs. The exact asset fields are not load-bearing for these tests.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter ReplayAbstractionsTests`
Expected: FAIL — `IReplaySource`/`ReplayRequest`/`IBackfillRequester` do not exist.

- [ ] **Step 3: Create the abstractions**

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/ReplayRequest.cs
using AlgoTradeForge.Domain;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Locates the source-record stream to replay. <see cref="Asset"/> resolves the on-disk asset dir
/// via the shared Infrastructure naming (AssetDirectoryName); <see cref="FromTs"/> is the resume
/// boundary (last completed bar's open ts).
/// </summary>
public readonly record struct ReplayRequest(Asset Asset, string Venue, string SourceFeedId, long FromTs);
```

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/IReplaySource.cs
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Yields archived source ticks at or after <see cref="ReplayRequest.FromTs"/>, in venue aggId
/// order, stitching recent relay segments with the deeper canonical archive. Venue-specific impl.
/// </summary>
public interface IReplaySource
{
    IAsyncEnumerable<TradeTick> Replay(ReplayRequest request, CancellationToken ct = default);
}
```

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/IBackfillRequester.cs
namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Attempts to make a detected gap available in the archive within <paramref name="policy"/>'s
/// budget. Returns true iff the archive now covers the gap (replay can re-read it contiguously).
/// Venue-specific: Binance issues REST backfill (generous budget); IB returns false fast (budget 0).
/// </summary>
public interface IBackfillRequester
{
    Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, CancellationToken ct = default);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter ReplayAbstractionsTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/Recovery/ReplayRequest.cs src/AlgoTradeForge.LiveHost.Application/Live/Recovery/IReplaySource.cs src/AlgoTradeForge.LiveHost.Application/Live/Recovery/IBackfillRequester.cs tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/ReplayAbstractionsTests.cs
git commit -F - <<'EOF'
feat(livehost): IReplaySource + IBackfillRequester + ReplayRequest seams
EOF
```

---

### Task 4: `CatchupCoordinator` — contiguous, deduped, gap-resolved tick stream

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/CatchupCoordinator.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/CatchupCoordinatorTests.cs`

**Interfaces:**
- Consumes: `IReplaySource`, `IBackfillRequester`, `RecoveryPolicy`, `ICatchupGate`/`SequenceWatermarkGate`, `Discontinuity`, `TradeTick`, the `FakeReplaySource`/`FakeBackfillRequester` from Task 3.
- Produces:
  - `sealed class CatchupCoordinator(IReplaySource replay, IBackfillRequester backfill, RecoveryPolicy policy)`
  - `IAsyncEnumerable<TradeTick> StreamFromBoundary(ReplayRequest request, ICatchupGate gate, Action<Discontinuity> onDiscontinuity, CancellationToken ct = default)`
  - Behaviour: pulls from `replay.Replay(request)`, admits each through `gate`; yields `Accept` ticks; drops `Duplicate`; on `Gap`, calls `backfill.TryBackfill` — if it returns true, re-issues replay from the gap's low boundary so the bridged records flow contiguously (each DISTINCT gap attempted once, keyed by `gap.FromTs`); if false (or the same gap recurs — a backfill that didn't actually close it), emits a `Discontinuity` via `onDiscontinuity`, `gate.Reseed(in tick)`, and yields the tick (the new contiguous run starts here). Multiple gaps in one window are handled; the per-gap-key guard prevents infinite recursion.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/CatchupCoordinatorTests.cs
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;
using static AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery.ReplayAbstractionsTests;

namespace AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;

public class CatchupCoordinatorTests
{
    private static ReplayRequest Req() => new(Btc(), "binance", "ticks", FromTs: 0);

    [Fact]
    public async Task Contiguous_stream_passes_through_and_dedupes_overlap()
    {
        // Replay yields 10,11,11,12 (a duplicate); output must be 10,11,12.
        var src = new FakeReplaySource(new[] { Tick(10), Tick(11), Tick(11), Tick(12) });
        var coord = new CatchupCoordinator(src, new FakeBackfillRequester(closes: false), RecoveryPolicy.NoBackfill);
        var gate = new SequenceWatermarkGate();

        var got = new List<long>();
        await foreach (var t in coord.StreamFromBoundary(Req(), gate, _ => { })) got.Add(t.Sequence);

        Assert.Equal(new long[] { 10, 11, 12 }, got);
    }

    [Fact]
    public async Task Unbridgeable_gap_declares_discontinuity_and_resumes()
    {
        // Replay yields 10, then jumps to 20. Budget zero -> declare + resume at 20.
        var src = new FakeReplaySource(new[] { Tick(10, ts: 100), Tick(20, ts: 200) });
        var coord = new CatchupCoordinator(src, new FakeBackfillRequester(closes: false), RecoveryPolicy.NoBackfill);
        var gate = new SequenceWatermarkGate();

        var declared = new List<Discontinuity>();
        var got = new List<long>();
        await foreach (var t in coord.StreamFromBoundary(Req(), gate, declared.Add)) got.Add(t.Sequence);

        Assert.Equal(new long[] { 10, 20 }, got);          // both delivered; 20 starts the new run
        var d = Assert.Single(declared);
        Assert.Equal(100, d.FromTs);                        // last-good tick (seq 10) ts
        Assert.Equal(200, d.ToTs);                          // first-after-gap tick (seq 20) ts
        Assert.Equal(DiscontinuityReason.MissingArchive, d.Reason);
    }

    [Fact]
    public async Task Multiple_distinct_gaps_are_each_backfilled_into_a_contiguous_stream()
    {
        // Seq 10, 50, 90 with gaps 10->50 and 50->90 (ts == seq here). The filling backfill closes each.
        var src = new MutableReplaySource(new[] { Tick(10, 10), Tick(50, 50), Tick(90, 90) });
        var backfill = new FillingBackfill(src);
        var coord = new CatchupCoordinator(src, backfill, new RecoveryPolicy(TimeSpan.FromSeconds(5), TimeSpan.Zero));
        var gate = new SequenceWatermarkGate();

        var declared = new List<Discontinuity>();
        var seqs = new List<long>();
        await foreach (var t in coord.StreamFromBoundary(Req(), gate, declared.Add)) seqs.Add(t.Sequence);

        Assert.Empty(declared);                                            // both gaps bridged, none declared
        Assert.Equal(2, backfill.Calls);                                   // one attempt per distinct gap
        Assert.Equal(Enumerable.Range(10, 81).Select(i => (long)i), seqs); // 10..90 contiguous
    }

    [Fact]
    public async Task Backfill_reporting_success_without_closing_attempts_once_then_declares()
    {
        // Persistent gap 10->20 that backfill claims to close but never does (static source).
        var src = new FakeReplaySource(new[] { Tick(10, ts: 100), Tick(20, ts: 200) });
        var backfill = new FakeBackfillRequester(closes: true);
        var coord = new CatchupCoordinator(src, backfill, new RecoveryPolicy(TimeSpan.FromSeconds(5), TimeSpan.Zero));
        var gate = new SequenceWatermarkGate();

        var declared = new List<Discontinuity>();
        var seqs = new List<long>();
        await foreach (var t in coord.StreamFromBoundary(Req(), gate, declared.Add)) seqs.Add(t.Sequence);

        Assert.Equal(1, backfill.Calls);                    // exactly one attempt — no infinite recursion
        var d = Assert.Single(declared);                    // then declared
        Assert.Equal(100, d.FromTs);
        Assert.Equal(200, d.ToTs);
        Assert.Equal(new long[] { 10, 20 }, seqs);
    }
}

// A replay source whose backing list can be mutated to simulate the archive gaining bridge records.
internal sealed class MutableReplaySource(IEnumerable<TradeTick> seed) : IReplaySource
{
    public List<TradeTick> Ticks { get; } = seed.ToList();

    public async IAsyncEnumerable<TradeTick> Replay(
        ReplayRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken ct = default)
    {
        foreach (var t in Ticks.OrderBy(x => x.Sequence).ToList()) // snapshot per call
        {
            if (t.TimestampMs >= request.FromTs) yield return t;
            await Task.Yield();
        }
    }
}

// Backfill that fills the missing contiguous sequences inside [gap] into the source (ts == seq here),
// then reports success — simulating a REST backfill that actually closes the gap.
internal sealed class FillingBackfill(MutableReplaySource src) : IBackfillRequester
{
    public int Calls { get; private set; }

    public Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, System.Threading.CancellationToken ct = default)
    {
        Calls++;
        for (var s = gap.FromTs + 1; s < gap.ToTs; s++)
            if (!src.Ticks.Any(t => t.Sequence == s)) src.Ticks.Add(Tick(s, s));
        return Task.FromResult(true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter CatchupCoordinatorTests`
Expected: FAIL — `CatchupCoordinator` does not exist.

- [ ] **Step 3: Create the coordinator**

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/CatchupCoordinator.cs
using System.Runtime.CompilerServices;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Drives <see cref="IReplaySource"/> through an <see cref="ICatchupGate"/> into one ordered,
/// deduped tick stream. On a gap it applies policy B: request backfill, and if the archive can't
/// bridge it within budget, declare a <see cref="Discontinuity"/> and resume from the new boundary.
/// The owning bar source feeds the yielded ticks into its accumulator (applying its own
/// suppress-known-bars rule); this type knows nothing about bars.
/// </summary>
public sealed class CatchupCoordinator(IReplaySource replay, IBackfillRequester backfill, RecoveryPolicy policy)
{
    public IAsyncEnumerable<TradeTick> StreamFromBoundary(
        ReplayRequest request,
        ICatchupGate gate,
        Action<Discontinuity> onDiscontinuity,
        CancellationToken ct = default)
        => Stream(request, gate, onDiscontinuity, lastAttemptedFromTs: long.MinValue, ct);

    // lastAttemptedFromTs keys the gap we last tried to backfill (by its low boundary), threaded
    // through the re-replay recursion so (a) DISTINCT later gaps each get their own attempt, and
    // (b) re-encountering the SAME gap — a backfill that reported success but did not actually
    // close it — is NOT retried (no infinite recursion); it declares instead.
    private async IAsyncEnumerable<TradeTick> Stream(
        ReplayRequest request,
        ICatchupGate gate,
        Action<Discontinuity> onDiscontinuity,
        long lastAttemptedFromTs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var tick in replay.Replay(request, ct).ConfigureAwait(false))
        {
            switch (gate.Admit(in tick))
            {
                case TickAdmission.Accept:
                    yield return tick;
                    break;
                case TickAdmission.Duplicate:
                    break;
                case TickAdmission.Gap:
                    // Time window from the last-good tick (gate.LastTimestampMs) to this one.
                    var gap = new Discontinuity(
                        gate.LastTimestampMs, tick.TimestampMs, DiscontinuityReason.MissingArchive);

                    // Attempt backfill once per distinct gap; budget zero short-circuits in the requester.
                    if (policy.BackfillBudget > TimeSpan.Zero
                        && gap.FromTs != lastAttemptedFromTs
                        && await backfill.TryBackfill(request, gap, policy, ct).ConfigureAwait(false))
                    {
                        // Bridge records are now archived but THIS enumerator is past them. Re-replay
                        // from the gap's low boundary (the gate dedupes the re-read prefix); a later
                        // distinct gap still gets its own attempt, the SAME gap does not.
                        await foreach (var b in Stream(
                            request with { FromTs = gap.FromTs }, gate, onDiscontinuity, gap.FromTs, ct).ConfigureAwait(false))
                            yield return b;
                        yield break;
                    }

                    onDiscontinuity(gap);
                    gate.Reseed(in tick);
                    yield return tick;
                    break;
            }
        }
    }
}
```

> Multiple distinct gaps in one window are each backfilled (the re-replay recursion handles one per level; the gate dedupes each re-read prefix). The `gap.FromTs != lastAttemptedFromTs` guard bounds the recursion: a backfill that reports success without actually closing a gap is retried at most once, then declared — no infinite loop. Cost is O(gaps × tail) re-reads, acceptable for the rare multi-gap case on slow alt-bar streams (a bounded `[from,to]` sub-replay would make it O(tail) — an optimization, not correctness).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter CatchupCoordinatorTests`
Expected: PASS (all four cases).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/Recovery/CatchupCoordinator.cs tests/AlgoTradeForge.LiveHost.Application.Tests/Live/Recovery/CatchupCoordinatorTests.cs
git commit -F - <<'EOF'
feat(livehost): CatchupCoordinator — contiguous deduped gap-resolved stream
EOF
```

---

### Task 5: `CatchupPlan` + catch-up-aware `TickAggregationBarSource` (cold start + partial)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/Recovery/CatchupPlan.cs`
- Modify: `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/TickAggregationBarSourceCatchupTests.cs`

**Interfaces:**
- Consumes: `CatchupCoordinator`, `ICatchupGate`/`SequenceWatermarkGate`, `ReplayRequest`, `Discontinuity`, `IInt64BarLoader` (`AlgoTradeForge.Application.CandleIngestion`), `DataFeedDescriptor`, `Int64Bar`, `AccumulatorEntry.Open`, `TickToSourceRecord`.
- Produces:
  - `sealed record CatchupPlan(CatchupCoordinator Coordinator, ReplayRequest Request, IInt64BarLoader WarmupLoader, DataFeedDescriptor AltBarFeed, int WarmupBarCount, Action<Discontinuity>? OnDiscontinuity = null)`
  - `TickAggregationBarSource` gains a constructor overload taking an optional `CatchupPlan? catchup`. When non-null, `Start()` runs the cold-start sequence; `Feed` buffers while catching up, then drains and goes live.
  - New behaviour on `Start()`: load last `WarmupBarCount` completed bars → seed `Recent` (NOT dispatched — they predate the session); resume boundary = last warmup bar's `TimestampMs`; replay via the coordinator into the accumulator; **suppress** emitted bars with `TsMs <= boundary`; dispatch bars with `TsMs > boundary`; drain the buffered live ticks; set state Live.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/TickAggregationBarSourceCatchupTests.cs
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;
using static AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery.ReplayAbstractionsTests;

namespace AlgoTradeForge.LiveHost.Application.Tests.DataPlane;

public class TickAggregationBarSourceCatchupTests
{
    private static ScaleContext Scale() => new(tickSize: 0.01m);

    // EqV threshold 40. Warmup feed already produced a completed bar at ts=1000 (open ts).
    // Replay covers source records from boundary=1000 forward; first re-derived bar (ts<=1000) is
    // suppressed (already known), the partial continues into live ticks.
    private static TradeTick Tick(long seq, long ts, long price, long qty) =>
        new(ts, price, qty, seq, AggressorSide.Buy);

    [Fact]
    public async Task Cold_start_seeds_recent_suppresses_known_bar_and_continues_partial()
    {
        // Persisted alt-bar feed has one completed bar opening at ts=1000.
        var warmupBar = new Int64Bar(1000, 5_000_000, 5_000_100, 4_999_900, 5_000_050, 40);
        var loader = new SingleBarLoader(warmupBar);

        // Replay re-derives the known bar (40 units crossing at seq 11) then 30 units of a NEW partial.
        var replayTicks = new[]
        {
            Tick(10, 1000, 5_000_000, 25),   // part of known bar
            Tick(11, 1000, 5_000_050, 15),   // crosses 40 -> re-derives known bar (open ts 1000) -> SUPPRESSED
            Tick(12, 1001, 5_000_050, 30),   // opens NEW partial (not yet 40)
        };
        var coord = new CatchupCoordinator(new FakeReplaySource(replayTicks), new FakeBackfillRequester(false), RecoveryPolicy.NoBackfill);

        var dispatched = new List<Int64Bar>();
        var plan = new CatchupPlan(
            coord,
            new ReplayRequest(Btc(), "binance", "ticks", FromTs: 0), // FromTs set by Start() from boundary
            loader,
            new DataFeedDescriptor("root", "binance", "BTCUSDT_perp", "EqV_40", DataFeedKind.AltBar),
            WarmupBarCount: 256);

        var src = new TickAggregationBarSource("EqV", frozenThreshold: 40, Scale(),
            onBar: (b, _) => dispatched.Add(b), catchup: plan);

        await src.Start();

        // Recent seeded with the warmup bar; no NEW completed bar dispatched yet (partial = 30 < 40).
        Assert.Contains(warmupBar, src.Recent);
        Assert.Empty(dispatched);

        // 15 more live units (seq 13) crosses 40 -> the partial (30) + 15 completes a NEW bar.
        src.Feed(Tick(13, 1002, 5_000_060, 15));
        var bar = Assert.Single(dispatched);
        Assert.True(bar.TimestampMs > 1000, "new bar must open after the suppressed known bar");
    }
}

file sealed class SingleBarLoader(Int64Bar bar) : IInt64BarLoader
{
    public Task<TimeSeries<Int64Bar>> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to, System.Threading.CancellationToken ct = default)
    {
        var s = new TimeSeries<Int64Bar>();
        s.Add(bar);
        return Task.FromResult(s);
    }
    public Task<DateTimeOffset?> GetLastTimestamp(DataFeedDescriptor feed, System.Threading.CancellationToken ct = default) =>
        Task.FromResult<DateTimeOffset?>(DateTimeOffset.FromUnixTimeMilliseconds(bar.TimestampMs));
}
```

> Confirm `IInt64BarLoader`'s exact method set against `src/AlgoTradeForge.Application/CandleIngestion/IInt64BarLoader.cs` and adjust `SingleBarLoader` to implement every member. `TimeSeries<Int64Bar>.Add` is the existing append (see `src/AlgoTradeForge.Domain/History/`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter TickAggregationBarSourceCatchupTests`
Expected: FAIL — `CatchupPlan` and the `catchup:` constructor parameter do not exist.

- [ ] **Step 3: Create `CatchupPlan`**

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/Recovery/CatchupPlan.cs
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Everything a tick-aggregation bar source needs to catch up to the present at session start:
/// the warmup-bar loader (completed bars read cheaply from the persisted alt-bar feed), the replay
/// coordinator + request (tail source-record replay), and how many warmup bars to seed.
/// </summary>
public sealed record CatchupPlan(
    CatchupCoordinator Coordinator,
    ReplayRequest Request,
    IInt64BarLoader WarmupLoader,
    DataFeedDescriptor AltBarFeed,
    int WarmupBarCount,
    Action<Discontinuity>? OnDiscontinuity = null);
```

- [ ] **Step 4: Rewrite `TickAggregationBarSource` to be catch-up-aware**

Replace the whole file body with:

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public sealed class TickAggregationBarSource : ITickDrivenBarSource
{
    private enum Phase { Cold, CatchingUp, Live }

    private readonly IBarAccumulator _acc;
    private readonly Action<Int64Bar, bool> _onBar;
    private readonly Queue<Int64Bar> _recent;
    private readonly int _recentCapacity;
    private readonly Lock _gate = new();
    private readonly CatchupPlan? _catchup;
    private readonly ICatchupGate _watermark = new SequenceWatermarkGate();

    // Live ticks that arrive during catch-up are buffered, then drained in order.
    private readonly Queue<TradeTick> _buffer = new();
    private Phase _phase;
    private long _suppressBarsAtOrBefore = long.MinValue; // bars at/under this open-ts predate the session

    public TickAggregationBarSource(
        string typeCode, long frozenThreshold, ScaleContext scale, Action<Int64Bar, bool> onBar,
        int recentCapacity = 256, CatchupPlan? catchup = null)
    {
        ArgumentNullException.ThrowIfNull(onBar);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(recentCapacity);

        _acc = AccumulatorEntry.Open(typeCode, frozenThreshold, scale, scale, DataFeedKind.Tick);
        _onBar = onBar;
        _recentCapacity = recentCapacity;
        _recent = new Queue<Int64Bar>(recentCapacity);
        _catchup = catchup;
        _phase = catchup is null ? Phase.Live : Phase.Cold;
    }

    public IReadOnlyList<Int64Bar> Recent { get { lock (_gate) return _recent.ToArray(); } }

    public async Task Start()
    {
        if (_catchup is null) return;
        _phase = Phase.CatchingUp;

        // 1. Seed Recent with completed warmup bars (read from the persisted alt-bar feed).
        var warmup = await _catchup.WarmupLoader.Load(
            _catchup.AltBarFeed, DateOnly.MinValue, DateOnly.MaxValue);
        Int64Bar? lastWarmup = null;
        foreach (var bar in TakeLast(warmup, _catchup.WarmupBarCount))
        {
            PushRecent(bar);          // NOT dispatched: predates the session
            lastWarmup = bar;
        }
        _suppressBarsAtOrBefore = lastWarmup?.TimestampMs ?? long.MinValue;

        // 2. Replay source records from the boundary; suppress re-derived known bars, dispatch new ones.
        var request = _catchup.Request with { FromTs = _suppressBarsAtOrBefore };
        await foreach (var tick in _catchup.Coordinator.StreamFromBoundary(
            request, _watermark, _catchup.OnDiscontinuity ?? (_ => { })))
        {
            FeedAccumulator(in tick, replaying: true);
        }

        // 3. Drain live ticks buffered during catch-up through the same watermark, then go live.
        lock (_gate)
        {
            while (_buffer.Count > 0)
            {
                var t = _buffer.Dequeue();
                FeedAccumulator(in t, replaying: false);
            }
            _phase = Phase.Live;
        }
    }

    public void Feed(in TradeTick tick)
    {
        if (_phase == Phase.CatchingUp)
        {
            lock (_gate) { if (_phase == Phase.CatchingUp) { _buffer.Enqueue(tick); return; } }
        }
        FeedAccumulator(in tick, replaying: false);
    }

    private void FeedAccumulator(in TradeTick tick, bool replaying)
    {
        // Live ticks pass the watermark (dedupe vs replay); replayed ticks were already admitted
        // by the coordinator's gate, so re-admitting a live tick is the only place dedupe matters.
        if (!replaying && _watermark.Admit(in tick) != TickAdmission.Accept)
            return;

        var rec = TickToSourceRecord.From(in tick);
        if (_acc.TryAdvance(in rec, out var bar))
            Emit(ToInt64Bar(in bar));
        while (_acc.TryDrainQueued(out var extra))
            Emit(ToInt64Bar(in extra));
    }

    private void Emit(Int64Bar bar)
    {
        PushRecent(bar);
        if (bar.TimestampMs <= _suppressBarsAtOrBefore) return; // re-derived known bar — do not dispatch
        _onBar(bar, false);
    }

    private void PushRecent(Int64Bar bar)
    {
        lock (_gate)
        {
            if (_recent.Count >= _recentCapacity) _recent.Dequeue();
            _recent.Enqueue(bar);
        }
    }

    private static IEnumerable<Int64Bar> TakeLast(TimeSeries<Int64Bar> series, int n)
    {
        var count = series.Count;
        var start = count > n ? count - n : 0;
        for (var i = start; i < count; i++) yield return series[i];
    }

    private static Int64Bar ToInt64Bar(in AggregatedBar b) =>
        new(b.TsMs, b.Open, b.High, b.Low, b.Close, b.Volume);
}
```

> Confirm `TimeSeries<Int64Bar>` exposes `Count` + indexer (it backs the existing loaders); if it only enumerates, materialize with `ToList()` in `TakeLast`. Confirm `Int64Bar`'s ctor order `(ts,o,h,l,c,vol)` against `src/AlgoTradeForge.Domain/History/Int64Bar.cs`.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter TickAggregationBarSourceCatchupTests`
Expected: PASS.

- [ ] **Step 6: Build the solution**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: 0 errors. The no-catchup constructor path is unchanged (cold `_phase = Live`), so `BarSourceResolver` (Task 9 rewires it) still compiles against the existing 4-arg ctor.

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/Recovery/CatchupPlan.cs src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/TickAggregationBarSourceCatchupTests.cs
git commit -F - <<'EOF'
feat(livehost): catch-up-aware TickAggregationBarSource (cold start + partial)
EOF
```

---

### Task 6: Mid-session reconnect — gap-triggered single-flight recovery

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/TickAggregationBarSourceReconnectTests.cs`

**Interfaces:**
- Consumes: everything from Task 5.
- Produces: on a `Feed` whose watermark returns `Gap`, the source switches to `CatchingUp`, buffers subsequent ticks, runs a single-flight `StreamFromBoundary` from the last-emitted bar's open ts, drains, returns to `Live`. Concurrent gap triggers are coalesced (single-flight latch).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/TickAggregationBarSourceReconnectTests.cs
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using Xunit;
using static AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery.ReplayAbstractionsTests;

namespace AlgoTradeForge.LiveHost.Application.Tests.DataPlane;

public class TickAggregationBarSourceReconnectTests
{
    private static ScaleContext Scale() => new(tickSize: 0.01m);
    private static TradeTick T(long seq, long ts, long price, long qty) => new(ts, price, qty, seq, AggressorSide.Buy);

    [Fact]
    public async Task Gap_in_live_stream_triggers_recovery_then_resumes_live()
    {
        // No cold catch-up; go live, take contiguous ticks, then a gap. The reconnect replay
        // (FromTs = last emitted bar open) bridges seq 11..14, after which live resumes at 15.
        var bridge = new[] { T(11, 1001, 5_000_000, 20), T(12, 1002, 5_000_000, 20),
                             T(13, 1003, 5_000_000, 20), T(14, 1004, 5_000_000, 20) };
        var coord = new CatchupCoordinator(new FakeReplaySource(bridge), new FakeBackfillRequester(false), RecoveryPolicy.NoBackfill);

        var dispatched = new List<Int64Bar>();
        var plan = new CatchupPlan(coord,
            new ReplayRequest(Btc(), "binance", "ticks", 0),
            warmupLoaderEmpty: out var _,  // placeholder; see note
            altBarFeed: default, warmupBarCount: 0);

        // For the reconnect-only path use the dedicated ctor that takes a recovery plan but no warmup.
        var src = TickAggregationBarSource.ForReconnectTest("EqV", 40, Scale(), (b, _) => dispatched.Add(b), coord,
            new ReplayRequest(Btc(), "binance", "ticks", 0));

        src.Feed(T(10, 1000, 5_000_000, 20)); // seq 10 contiguous, opens a bar
        src.Feed(T(40, 5000, 5_000_000, 20)); // seq jump 10 -> 40 = GAP, triggers recovery

        await src.WaitForRecoveryIdle();       // test hook: await the single-flight recovery task

        // After recovery the source is Live again and the watermark advanced past the bridge.
        Assert.True(src.IsLive);
    }
}
```

> NOTE: this test needs two small test-only hooks on the source — a simplified constructor/factory `ForReconnectTest(...)` (recovery plan, no warmup loader) and `Task WaitForRecoveryIdle()` + `bool IsLive`. Implement them as `internal` and expose to the test project via `InternalsVisibleTo` (the test assembly is already a friend; confirm in the csproj and add `[assembly: InternalsVisibleTo("AlgoTradeForge.LiveHost.Application.Tests")]` if missing). The `warmupLoaderEmpty` placeholder line above is illustrative — delete it and use the factory.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter TickAggregationBarSourceReconnectTests`
Expected: FAIL — the reconnect path, factory, and hooks do not exist.

- [ ] **Step 3: Add the reconnect path**

Add to `TickAggregationBarSource`:
- A field `private readonly object _recoveryLatch = new();` and `private Task _recovery = Task.CompletedTask;` and `private long _lastEmittedOpenTs = long.MinValue;` (set in `Emit`).
- In `FeedAccumulator`, when a LIVE tick returns `TickAdmission.Gap`, call `TriggerRecovery(tick)` (single-flight) instead of dropping.

```csharp
    // set in Emit(): _lastEmittedOpenTs = bar.TimestampMs;

    private void TriggerRecovery(in TradeTick trigger)
    {
        var t = trigger;
        lock (_recoveryLatch)
        {
            if (_phase == Phase.CatchingUp) { lock (_gate) _buffer.Enqueue(t); return; }
            _phase = Phase.CatchingUp;
            lock (_gate) _buffer.Enqueue(t); // the gap tick rejoins the ordered stream after replay
            var request = _catchup is not null
                ? _catchup.Request with { FromTs = _lastEmittedOpenTs }
                : _reconnectRequest with { FromTs = _lastEmittedOpenTs };
            var coordinator = _catchup?.Coordinator ?? _reconnectCoordinator!;
            _recovery = Task.Run(() => RunRecovery(coordinator, request));
        }
    }

    private async Task RunRecovery(CatchupCoordinator coordinator, ReplayRequest request)
    {
        await foreach (var tick in coordinator.StreamFromBoundary(request, _watermark, _catchup?.OnDiscontinuity ?? (_ => { })))
            FeedAccumulator(in tick, replaying: true);

        lock (_gate)
        {
            while (_buffer.Count > 0) { var t = _buffer.Dequeue(); FeedAccumulator(in t, replaying: false); }
            _phase = Phase.Live;
        }
    }
```

Add the test hooks + reconnect-only fields/factory:

```csharp
    private readonly CatchupCoordinator? _reconnectCoordinator;
    private readonly ReplayRequest _reconnectRequest;

    internal static TickAggregationBarSource ForReconnectTest(
        string typeCode, long threshold, ScaleContext scale, Action<Int64Bar, bool> onBar,
        CatchupCoordinator coordinator, ReplayRequest request)
    {
        var s = new TickAggregationBarSource(typeCode, threshold, scale, onBar);
        // reflection-free: assign via a dedicated ctor in real code; for the test path we expose setters.
        return s.WithReconnect(coordinator, request);
    }

    internal TickAggregationBarSource WithReconnect(CatchupCoordinator coordinator, ReplayRequest request)
    {
        // _reconnectCoordinator/_reconnectRequest are set here (make them non-readonly or move to a ctor).
        // Keeps the reconnect trigger usable without a full CatchupPlan (no warmup).
        // Implementation detail: store and return this.
        return this;
    }

    internal bool IsLive => _phase == Phase.Live;
    internal Task WaitForRecoveryIdle() => _recovery;
```

> Implementation guidance: make `_reconnectCoordinator`/`_reconnectRequest` settable (drop `readonly`, or add a private ctor the factory calls). The cleanest production shape is a single ctor that always takes an optional `CatchupPlan` (cold + reconnect both ride it) — in production every catch-up-aware source HAS a `CatchupPlan`, so the reconnect path reads `_catchup.Coordinator`/`_catchup.Request` and the `_reconnect*` fields/factory exist only for the no-warmup unit test. Prefer collapsing to `_catchup` if the test can supply a zero-warmup `CatchupPlan` (a `SingleBarLoader` returning an empty series + `WarmupBarCount: 0`); if so, delete `_reconnect*`, `WithReconnect`, and `ForReconnectTest`, and build the plan in the test instead. Pick the collapse if it keeps one code path.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter TickAggregationBarSourceReconnectTests`
Expected: PASS.

- [ ] **Step 5: Run the full Application test project (no regressions)**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/TickAggregationBarSourceReconnectTests.cs
git commit -F - <<'EOF'
feat(livehost): gap-triggered single-flight reconnect recovery
EOF
```

---

### Task 7: Binance `IReplaySource` — relay `.atft` + canonical archive

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/RelayArchiveReplaySource.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/Recovery/RelayArchiveReplaySourceTests.cs`

**Interfaces:**
- Consumes: `IFileStorage` (`AlgoTradeForge.Storage`), `SegmentReader<TradeTick>` (`AlgoTradeForge.Live.Relay`), `SegmentWriter<TradeTick>` (for the test fixture), `AssetDirectoryName` (`AlgoTradeForge.Infrastructure.History`), `IReplaySource`, `ReplayRequest`, `TradeTick`.
- Produces: `sealed class RelayArchiveReplaySource(IFileStorage storage, string relayKeyPrefix) : IReplaySource`. Lists relay trade segments under `{relayKeyPrefix}/{venue}/{instrument}/trades/`, opens each via `SegmentReader<TradeTick>`, yields ticks with `TimestampMs >= request.FromTs` in segment order (segment filenames `{createdAtMs:D13}-{firstSequence:D19}.atft` sort chronologically). Instrument = `request.Asset` mapped via `AssetDirectoryName.From` is the canonical dir; the relay key uses the raw venue instrument symbol — for Binance crypto that is the asset name (e.g. `BTCUSDT`). The deeper canonical-CSV stitch is a documented extension point (see note) — this task ships the relay path, which covers the reconnect window and recent cold starts.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/Recovery/RelayArchiveReplaySourceTests.cs
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using AlgoTradeForge.Storage;
using Xunit;
using static AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery.ReplayAbstractionsTests;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.Recovery;

public class RelayArchiveReplaySourceTests
{
    [Fact]
    public async Task Replays_atft_segments_from_boundary_in_aggId_order()
    {
        using var tmp = new TempStorage(); // helper: LocalFileStorage over a temp DataRoot (see note)
        var venue = "binance"; var instrument = "BTCUSDT"; var prefix = "relay";

        // Write one trades segment via the relay's own writer so the bytes match SegmentReader.
        await WriteSegment(tmp.Storage, $"{prefix}/{venue}/{instrument}/trades",
            createdAtMs: 1000, firstSequence: 10,
            new[] { Tick(10, 1000), Tick(11, 1001), Tick(12, 1002) });

        var src = new RelayArchiveReplaySource(tmp.Storage, prefix);
        var req = new ReplayRequest(Btc(), venue, "ticks", FromTs: 1001);

        var seqs = new List<long>();
        await foreach (var t in src.Replay(req)) seqs.Add(t.Sequence);

        Assert.Equal(new long[] { 11, 12 }, seqs); // ts < 1001 (seq 10) filtered out
    }

    private static async Task WriteSegment(IFileStorage storage, string dirKey, long createdAtMs, long firstSequence, TradeTick[] ticks)
    {
        using var ms = new MemoryStream();
        // SegmentWriter<TradeTick> writes the SegmentHeader + frames; mirror RelayWriter's pipeline.
        // Confirm the exact SegmentWriter ctor/Write API against src/AlgoTradeForge.Live.Relay/SegmentWriter.cs.
        var writer = new SegmentWriter<TradeTick>(ms, instrumentId: 0, firstSequence, createdAtMs, leaveOpen: true);
        foreach (var t in ticks) writer.Write(in t);
        writer.Dispose();
        var name = $"{createdAtMs:D13}-{firstSequence:D19}.atft";
        await storage.WriteAllBytes($"{dirKey}/{name}", ms.ToArray());
    }
}
```

> NOTES:
> 1. Confirm `SegmentWriter<TradeTick>`'s constructor + write method names against `src/AlgoTradeForge.Live.Relay/SegmentWriter.cs` and adjust `WriteSegment`. The goal is bytes that `SegmentReader<TradeTick>` reads back (header + `PayloadSize`-byte frames).
> 2. `TempStorage` wraps `LocalFileStorage` over a temp dir. Reuse the existing storage test helper if the Infrastructure test project has one (search for `LocalFileStorage(` in `tests/`); otherwise create a small `IDisposable` fixture mirroring `LocalStorageOptions { DataRoot = <temp> }`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter RelayArchiveReplaySourceTests`
Expected: FAIL — `RelayArchiveReplaySource` does not exist.

- [ ] **Step 3: Create the replay source**

```csharp
// src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/RelayArchiveReplaySource.cs
using System.Runtime.CompilerServices;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

/// <summary>
/// Binance <see cref="IReplaySource"/>: streams archived trade ticks from relay <c>.atft</c>
/// segments under <c>{relayKeyPrefix}/{venue}/{instrument}/trades/</c>, oldest-first (segment
/// filenames sort chronologically), filtered to <see cref="ReplayRequest.FromTs"/>. The deeper
/// canonical-CSV stitch for cold starts older than relay retention is an extension point (below).
/// </summary>
public sealed class RelayArchiveReplaySource(IFileStorage storage, string relayKeyPrefix) : IReplaySource
{
    public async IAsyncEnumerable<TradeTick> Replay(
        ReplayRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Binance crypto: the relay instrument key is the asset name (e.g. "BTCUSDT").
        var instrument = request.Asset.Name;
        var dir = $"{relayKeyPrefix}/{request.Venue}/{instrument}/trades";

        var keys = new List<string>();
        await foreach (var key in storage.ListKeys(dir, suffix: ".atft", recursive: false, ct))
            keys.Add(key);
        keys.Sort(StringComparer.Ordinal); // {createdAtMs:D13}-{firstSequence:D19}.atft → chronological

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = await storage.OpenRead(key, ct).ConfigureAwait(false);
            using var reader = new SegmentReader<TradeTick>(stream, leaveOpen: false);
            while (reader.TryRead(out var tick))
            {
                if (tick.TimestampMs < request.FromTs) continue;
                yield return tick;
            }
        }
    }
}
```

> EXTENSION POINT (deferred, per spec open point #2): a cold start whose `FromTs` predates the relay's earliest retained segment needs the canonical tick archive (`{DataRoot}/{venue}/{assetDir}/ticks/`, `assetDir = AssetDirectoryName.From(request.Asset)`), read via the shared `AlgoTradeForge.Infrastructure` tick reader and stitched BEFORE the relay segments (same `FromTs` filter; the `ICatchupGate` dedupes the overlap). Wire this when cold-start-beyond-retention is exercised; the reconnect window and recent cold starts are covered by the relay path alone.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter RelayArchiveReplaySourceTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/RelayArchiveReplaySource.cs tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/Recovery/RelayArchiveReplaySourceTests.cs
git commit -F - <<'EOF'
feat(livehost): Binance relay-archive IReplaySource (.atft replay)
EOF
```

---

### Task 8: Binance `IBackfillRequester` — REST gap-close (seam + minimal impl)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/BinanceBackfillRequester.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/Recovery/BinanceBackfillRequesterTests.cs`

**Interfaces:**
- Consumes: `IBackfillRequester`, `ReplayRequest`, `Discontinuity`, `RecoveryPolicy`, a small `IAggTradeBackfillClient` seam (REST), `TimeProvider`.
- Produces:
  - `interface IAggTradeBackfillClient { Task<bool> FetchAndArchive(string instrument, long fromTs, long toTs, CancellationToken ct); }` (returns true when the gap range was fetched + written to the archive the replay source reads).
  - `sealed class BinanceBackfillRequester(IAggTradeBackfillClient client, TimeProvider time) : IBackfillRequester` — calls `FetchAndArchive`, polls per `policy.PollInterval` up to `policy.BackfillBudget`, returns true on success, false on timeout.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/Recovery/BinanceBackfillRequesterTests.cs
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery.ReplayAbstractionsTests;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.Recovery;

public class BinanceBackfillRequesterTests
{
    private static ReplayRequest Req() => new(Btc(), "binance", "ticks", 0);
    private static Discontinuity Gap() => new(1000, 2000, DiscontinuityReason.MissingArchive);

    [Fact]
    public async Task Returns_true_when_client_archives_the_gap()
    {
        var req = new BinanceBackfillRequester(new StubClient(succeeds: true), new FakeTimeProvider());
        Assert.True(await req.TryBackfill(Req(), Gap(), new RecoveryPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(1))));
    }

    [Fact]
    public async Task Returns_false_when_client_cannot_close_within_budget()
    {
        var req = new BinanceBackfillRequester(new StubClient(succeeds: false), new FakeTimeProvider());
        Assert.False(await req.TryBackfill(Req(), Gap(), RecoveryPolicy.NoBackfill));
    }

    private sealed class StubClient(bool succeeds) : IAggTradeBackfillClient
    {
        public Task<bool> FetchAndArchive(string instrument, long fromTs, long toTs, System.Threading.CancellationToken ct)
            => Task.FromResult(succeeds);
    }
}
```

> `FakeTimeProvider` is from `Microsoft.Extensions.TimeProvider.Testing` (already used in relay tests — confirm the package reference in the Infrastructure test csproj; the relay tests reference it). For `NoBackfill` (zero budget) the requester must short-circuit to false WITHOUT needing the clock to advance.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter BinanceBackfillRequesterTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Create the requester + client seam**

```csharp
// src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/IAggTradeBackfillClient.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

/// <summary>Fetches aggTrades for [fromTs,toTs] from the venue REST API and writes them into the
/// archive the replay source reads. Returns true when the range is now covered.</summary>
public interface IAggTradeBackfillClient
{
    Task<bool> FetchAndArchive(string instrument, long fromTs, long toTs, CancellationToken ct);
}
```

```csharp
// src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/BinanceBackfillRequester.cs
using AlgoTradeForge.LiveHost.Application.Live.Recovery;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

/// <summary>
/// Binance gap policy (B): request a REST aggTrade backfill of the gap and poll until the archive
/// covers it or the budget expires. Zero budget (IB-style) short-circuits to false.
/// </summary>
public sealed class BinanceBackfillRequester(IAggTradeBackfillClient client, TimeProvider time) : IBackfillRequester
{
    public async Task<bool> TryBackfill(ReplayRequest context, Discontinuity gap, RecoveryPolicy policy, CancellationToken ct = default)
    {
        if (policy.BackfillBudget <= TimeSpan.Zero) return false;

        var deadline = time.GetUtcNow() + policy.BackfillBudget;
        while (true)
        {
            if (await client.FetchAndArchive(context.Asset.Name, gap.FromTs, gap.ToTs, ct).ConfigureAwait(false))
                return true;
            if (time.GetUtcNow() >= deadline) return false;
            await Task.Delay(policy.PollInterval, time, ct).ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter BinanceBackfillRequesterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/IAggTradeBackfillClient.cs src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/BinanceBackfillRequester.cs tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/Recovery/BinanceBackfillRequesterTests.cs
git commit -F - <<'EOF'
feat(livehost): Binance backfill requester (policy B bounded wait)
EOF
```

---

### Task 9: Wire catch-up into `BarSourceResolver` + DI

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/BarSourceResolver.cs`
- Modify: `src/AlgoTradeForge.LiveHost.WebApi/Program.cs` (DI registrations + options)
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/CatchupOptions.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/BarSourceResolverCatchupTests.cs`

**Interfaces:**
- Consumes: `IReplaySource`, `IBackfillRequester`, `RecoveryPolicy`, `CatchupCoordinator`, `CatchupPlan`, `IInt64BarLoader`, `IAssetRepository` (to resolve the `Asset` for `ReplayRequest`/`AssetDirectoryName`), `AltBarFeedId`, `ThresholdResolver`.
- Produces: `BarSourceResolver.ResolveAltBar` builds a `CatchupPlan` (warmup loader + coordinator + request with `SourceFeedId`="ticks", `Venue`=exchange, `Asset`) and passes it to the `TickAggregationBarSource` ctor. A `CatchupOptions { int WarmupBarCount; TimeSpan BackfillBudget; TimeSpan PollInterval; string RelayKeyPrefix; string DataRoot; }` carries config. `RecoveryPolicy` is built from options (Binance: generous budget).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/BarSourceResolverCatchupTests.cs
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.DataPlane;

public class BarSourceResolverCatchupTests
{
    [Fact]
    public void AltBar_subscription_resolves_a_catchup_aware_tick_source()
    {
        var resolver = BarSourceResolverTestFactory.Create(); // builds resolver with fakes (see note)
        var scale = new ScaleContext(tickSize: 0.01m);

        var sub = new AltBarSubscription(/* feedId */ "EqV_40", /* asset binding per existing ctor */ default!);
        var source = resolver.Resolve("BTCUSDT", sub, scale, (_, _) => { });

        Assert.NotNull(source);
        Assert.IsType<TickAggregationBarSource>(source);
        // Catch-up wiring is exercised end-to-end in Task 10; here we assert the resolver produces
        // the tick-aggregation source for an alt-bar sub without throwing when catch-up deps are present.
    }
}
```

> NOTE: match `AltBarSubscription`'s real constructor (`src/AlgoTradeForge.Domain/Strategy/Subscriptions/AltBarSubscription.cs`) — the existing `BarSourceResolver.ResolveAltBar` reads `ab.FeedId`. `BarSourceResolverTestFactory` constructs the resolver with a `FakeReplaySource`/`FakeBackfillRequester`/`SingleBarLoader` and a `CatchupOptions`. Keep this test light; the behavioural proof is Task 10.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter BarSourceResolverCatchupTests`
Expected: FAIL — the resolver ctor does not yet take catch-up deps / factory missing.

- [ ] **Step 3: Add `CatchupOptions`**

```csharp
// src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/CatchupOptions.cs
namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;

public sealed class CatchupOptions
{
    public int WarmupBarCount { get; set; } = 256;
    public TimeSpan BackfillBudget { get; set; } = TimeSpan.FromSeconds(30); // Binance; IB sets 0
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public required string RelayKeyPrefix { get; set; }
    public required string DataRoot { get; set; }
}
```

- [ ] **Step 4: Rewire `BarSourceResolver`**

Modify `BarSourceResolver` to take the catch-up dependencies and build a `CatchupPlan` for alt-bar subs. Key changes:

```csharp
// constructor: add deps
public sealed class BarSourceResolver(
    BinanceWebSocketManager ws,
    IReplaySource replaySource,
    IBackfillRequester backfill,
    IInt64BarLoader warmupLoader,
    IAssetRepository assets,
    CatchupOptions options) : IBarSourceResolver
{
    // ... TimeBar / Tick cases unchanged ...

    private TickAggregationBarSource ResolveAltBar(
        string instrument, AltBarSubscription ab, ScaleContext scale, Action<Int64Bar, bool> onBar)
    {
        var feedId = AltBarFeedId.Parse(ab.FeedId);
        var frozenThreshold = ThresholdResolver.ResolveParsed(feedId.TypeCode, feedId.Threshold, scale);

        // Resolve the Asset for replay-source location + warmup-feed path. The asset is already
        // loaded at session start; here we re-resolve by name/exchange for the shared source.
        var asset = assets.GetByName(instrument, "binance")   // confirm sync/async accessor on IAssetRepository
            ?? throw new InvalidOperationException($"Asset '{instrument}' not found for catch-up.");
        var assetDir = AlgoTradeForge.Infrastructure.History.AssetDirectoryName.From(asset);

        var policy = new RecoveryPolicy(options.BackfillBudget, options.PollInterval);
        var coordinator = new CatchupCoordinator(replaySource, backfill, policy);
        var request = new ReplayRequest(asset, "binance", SourceFeedId: "ticks", FromTs: 0);
        var altBarFeed = new DataFeedDescriptor(options.DataRoot, "binance", assetDir, ab.FeedId, DataFeedKind.AltBar);

        var plan = new CatchupPlan(coordinator, request, warmupLoader, altBarFeed, options.WarmupBarCount);
        return new TickAggregationBarSource(feedId.TypeCode, frozenThreshold, scale, onBar, catchup: plan);
    }
}
```

> Confirm `IAssetRepository`'s accessor (`GetByName`/`GetByNameAsync`) against `src/AlgoTradeForge.Application/Repositories/`. If only async exists, resolve the asset in `EnsureSources` (which is async) and pass it through `IBarSourceResolver.Resolve` — add an `Asset` parameter to `Resolve` rather than re-fetching synchronously here. Prefer threading the already-resolved `Asset` from the session (it's loaded in `StartLiveSessionCommandHandler`) over a second lookup; adjust the `IBarSourceResolver.Resolve` signature accordingly and update `TickRouter.EnsureSources` to pass it.

- [ ] **Step 5: Register DI in `Program.cs`**

In `src/AlgoTradeForge.LiveHost.WebApi/Program.cs`, near the existing `IBarSourceResolver` registration, add:

```csharp
builder.Services.Configure<CatchupOptions>(builder.Configuration.GetSection("Catchup"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<CatchupOptions>>().Value);
builder.Services.AddSingleton<IReplaySource>(sp =>
    new RelayArchiveReplaySource(sp.GetRequiredService<IFileStorage>(), sp.GetRequiredService<CatchupOptions>().RelayKeyPrefix));
builder.Services.AddSingleton<IAggTradeBackfillClient, /* existing or stub */ NullAggTradeBackfillClient>();
builder.Services.AddSingleton<IBackfillRequester, BinanceBackfillRequester>();
```

> `NullAggTradeBackfillClient` (a `FetchAndArchive => Task.FromResult(false)` impl) is acceptable as the initial wiring — the seam exists; a real REST backfill client is a follow-up (spec open point #2). Create it next to `BinanceBackfillRequester`. Confirm `IFileStorage`/`IInt64BarLoader`/`IAssetRepository` are already registered in this composition root (they back the existing snapshot path); if `IInt64BarLoader` is not registered, add `AddSingleton<IInt64BarLoader, PartitionedCsvBarLoader>()`.

- [ ] **Step 6: Run the test + build**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter BarSourceResolverCatchupTests`
Then: `dotnet build AlgoTradeForge.slnx`
Expected: test PASS; build 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/Recovery/CatchupOptions.cs src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/BarSourceResolver.cs src/AlgoTradeForge.LiveHost.WebApi/Program.cs tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/BarSourceResolverCatchupTests.cs
git commit -F - <<'EOF'
feat(livehost): wire catch-up into BarSourceResolver + DI
EOF
```

---

### Task 10: Golden — batch ≡ catch-up ≡ live across a mid-bar restart

**Files:**
- Modify: `tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/BatchEqualsLiveGoldenTests.cs`

**Interfaces:**
- Consumes: the existing `SyntheticTicks`/`ThresholdFor`/`RunBatch`/`ToInt64Bar`/`TickToSourceRecord` helpers in this file (confirm names), `CatchupCoordinator`, `SequenceWatermarkGate`, `FakeReplaySource`, `TickAggregationBarSource`.

This proves the M6 property end-to-end through the catch-up path: a single uninterrupted batch run equals a run split inside an in-progress bar, where the second half is delivered via `IReplaySource` → `CatchupCoordinator` → `TickAggregationBarSource` (warmup-seeded + replay + live drain). Run for all 8 families.

- [ ] **Step 1: Add the failing golden test**

```csharp
    [Theory]
    [InlineData("EqV")] [InlineData("EqT")] [InlineData("EqD")]
    [InlineData("EqIV")] [InlineData("EqID")] [InlineData("EqIT")]
    [InlineData("Range")] [InlineData("Renko")]
    public async Task Catchup_run_equals_single_pass(string typeCode)
    {
        var ticks = SyntheticTicks(count: 4000);
        var threshold = ThresholdFor(typeCode);
        var scale = Scale();

        // Reference: single uninterrupted batch pass over all ticks.
        var reference = RunBatch(typeCode, threshold, scale, ticks);

        // Split inside a bar: batch the first half to find the last completed bar (warmup),
        // replay the remainder through the catch-up source.
        var split = ticks.Count / 2;
        var firstHalf = RunBatch(typeCode, threshold, scale, ticks.Take(split).ToList());
        Assert.True(firstHalf.Count > 0, $"{typeCode}: need at least one completed warmup bar.");
        var warmupBars = firstHalf;                      // List<Int64Bar>
        var boundary = warmupBars[^1].TimestampMs;

        // Replay = the source ticks whose aggId belongs to the partial+remainder, i.e. all ticks
        // from the boundary bar's open onward (the source records the live driver would consume).
        var replayTicks = ticks.Where(t => t.TimestampMs >= boundary).ToList();

        var dispatched = new List<Int64Bar>(warmupBars);  // strategy "sees" warmup + new
        var coord = new CatchupCoordinator(new FakeReplaySource(replayTicks), new FakeBackfillRequester(false), RecoveryPolicy.NoBackfill);
        var plan = new CatchupPlan(coord, new ReplayRequest(Btc(), "binance", "ticks", 0),
            new ListBarLoader(warmupBars), new DataFeedDescriptor("r", "binance", "BTCUSDT_perp", "feed", DataFeedKind.AltBar),
            WarmupBarCount: 10_000);

        var src = new TickAggregationBarSource(typeCode, threshold, scale,
            onBar: (b, _) => dispatched.Add(b), catchup: plan);
        await src.Start();

        Assert.Equal(reference, dispatched); // element-wise Int64Bar equality across the seam
    }
```

> `ListBarLoader` is a multi-bar variant of Task 5's `SingleBarLoader` (adds all `warmupBars`). `FakeReplaySource`/`Btc()`/`CatchupCoordinator` come from the Recovery test namespace — add the `using`. `SyntheticTicks` must return `TradeTick`s with **contiguous `Sequence`** values (so the watermark stays contiguous); if the existing helper doesn't set `Sequence`, extend it to assign `i` as the aggId. Confirm `RunBatch` returns `List<Int64Bar>` and that `ticks` are `TradeTick`.

- [ ] **Step 2: Run to verify it passes (seams already implemented)**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter BatchEqualsLiveGoldenTests`
Expected: PASS for all 8 families. A failure localizes to that family's `TryAdvance` determinism or the suppress-boundary math — fix there, never weaken the assertion.

- [ ] **Step 3: Commit**

```bash
git add tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/BatchEqualsLiveGoldenTests.cs
git commit -F - <<'EOF'
test(livehost): M6 golden — batch ≡ catch-up ≡ live across a mid-bar restart
EOF
```

---

### Task 11: Data-plane integration test + full-suite green

**Files:**
- Create: `tests/AlgoTradeForge.LiveHost.WebApi.Tests/DataPlane/CatchupDataPlaneTests.cs` (or extend `DataPlaneEndToEndTests.cs`)

**Interfaces:**
- Consumes: the existing data-plane end-to-end harness (`DataPlaneEndToEndTests` patterns — `TickRouter`, `StrategyDispatch`, a test strategy), `RelayArchiveReplaySource` over a temp storage with pre-written `.atft`, the real `BarSourceResolver` + catch-up wiring.

This proves the shared-source semantics: two sessions on the SAME `(instrument, spec)` get one catch-up (single-flight) and identical dispatched bars; a session joining mid-recovery never sees a half-seeded `Recent`.

- [ ] **Step 1: Write the integration test**

```csharp
// tests/AlgoTradeForge.LiveHost.WebApi.Tests/DataPlane/CatchupDataPlaneTests.cs
// Mirror DataPlaneEndToEndTests setup: build TickRouter(resolver, dispatch, logger),
// register two sessions whose subscriptions share (BTCUSDT, AltBar "EqV_40"),
// EnsureSources for both, assert:
//   - the shared source ran catch-up exactly once (one IReplaySource.Replay invocation — use a
//     counting fake), and
//   - both sessions' strategies received identical OnBarComplete bars.
// Confirm the exact harness entry points against tests/.../DataPlane/DataPlaneEndToEndTests.cs.
```

> This test is structural; write it against the real `DataPlaneEndToEndTests` helpers (do not invent a parallel harness). Use a counting `IReplaySource` wrapper to assert single-flight. If the existing harness can't easily host two overlapping sessions, assert single-flight at the `TickRouter` level (RefCount == 2, one source instance) plus the Task 10 golden for bar-equality, and note the reduced scope in the test comment.

- [ ] **Step 2: Run the test**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/ --filter CatchupDataPlaneTests`
Expected: PASS.

- [ ] **Step 3: Run every affected suite sequentially (ONE dotnet at a time)**

```bash
dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/
```
```bash
dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/
```
```bash
dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/
```
```bash
dotnet test tests/AlgoTradeForge.Domain.Tests/
```
Expected: all PASS. Then a full solution build:
```bash
dotnet build AlgoTradeForge.slnx
```
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add tests/AlgoTradeForge.LiveHost.WebApi.Tests/DataPlane/CatchupDataPlaneTests.cs
git commit -F - <<'EOF'
test(livehost): catch-up data-plane integration — shared-source single-flight
EOF
```

---

## Self-Review

**Spec coverage:**
- Unified seed+catch-up → Tasks 5 (cold) + 6 (reconnect). ✓
- Two streams, one accumulator (warmup read + tail replay) → Task 5 (`Start()` seeds Recent from loader, replays tail). ✓
- Watermark contiguity → Task 2 + integration in Task 5/6. ✓
- Gap policy B (per-venue budget) → Task 4 (declare/bridge) + Task 8 (Binance bounded wait; budget 0 ⇒ declare). ✓
- Shared recovery vocabulary → Task 1; consumed by data-plane impls (order-plane impl is M3b, out of scope). ✓
- Identity by mapping → Task 9 (`AssetDirectoryName.From` + `Asset` threaded through `Resolve`). ✓
- Layering (abstraction → base → venue impl) → Application `Recovery/` (abstractions `ICatchupGate`/`IReplaySource`/`IBackfillRequester` + `CatchupCoordinator`/`SequenceWatermarkGate` base) + Infrastructure Binance impls. ✓
- Shared-source / multi-strategy + single-flight latch → Task 6 (latch) + Task 11 (integration). ✓
- Extensibility (no per-type serialization) → inherent (replay feeds `TryAdvance`); golden parametrized over all 8 families (Task 10). New types need no catch-up change. ✓
- Resume boundary = `LastBarTs` → Task 5 derives it from the last loaded warmup bar (no manifest parse needed). ✓
- Verification: inline golden (suppress + equality) within Task 5/10; restart golden Task 10; dedupe Task 2; gap policy Task 4/8; shared-source Task 11; relay round-trip reused via real `SegmentReader` Task 7. ✓
- FE observation → enabled/deferred (spec); no task (correctly out of scope). ✓

**Deferred (flagged in spec, not in this plan):** canonical-CSV deep stitch beyond relay retention (Task 7 extension note), real REST backfill client (Task 9 uses `NullAggTradeBackfillClient`), IB impl + IB drop-signal (Plan 3/4), live-buffer overflow policy (bounded `Queue`; spec open point #1).

**Type consistency:** `TickAdmission {Accept,Duplicate,Gap}`, `Admit(in TradeTick)`, `Reseed(in TradeTick)`, `ICatchupGate.LastTimestampMs` (impl `SequenceWatermarkGate`), `Discontinuity(FromTs, ToTs, Reason)`, `StreamFromBoundary(ReplayRequest, ICatchupGate, Action<Discontinuity>, ct)`, `CatchupPlan(Coordinator, Request, WarmupLoader, AltBarFeed, WarmupBarCount, OnDiscontinuity?)`, `ReplayRequest(Asset, Venue, SourceFeedId, FromTs)`, `IBackfillRequester.TryBackfill(ReplayRequest, Discontinuity, RecoveryPolicy, ct)` — used consistently across Tasks 1–11.

**Known confirmation points (flagged inline for the implementer, NOT placeholders):** `CryptoPerpetualAsset.Create` shape (T3); `IInt64BarLoader` member set + `TimeSeries<Int64Bar>` `Count`/indexer (T5); `SegmentWriter<TradeTick>` API (T7); `IAssetRepository` sync vs async accessor (T9, with the preferred fix = thread the already-resolved `Asset` through `IBarSourceResolver.Resolve`); existing `DataPlaneEndToEndTests` harness entry points (T11). Each is a "verify the neighboring signature" step, with the fallback spelled out.
