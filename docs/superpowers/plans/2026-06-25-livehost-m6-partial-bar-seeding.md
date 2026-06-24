# M6 Partial-Bar Seeding (Plan 0) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Generalize the alt-bar accumulator resume seam beyond Renko's single `long` so live accumulators can be seeded with the full mid-bar (partial) state of any family, persisted as JSON on `IFileStorage`, closing vision M6 (live bars continue the historical series).

**Architecture:** Each `IBarAccumulator` exposes a polymorphic `TrySaveState`/`RestoreState` pair over a typed `AccumulatorState` Domain record (one per family). A thin Domain codec (`AccumulatorStateJson`) maps `{typeCode, state}` ↔ JSON, resolving the concrete state type by `typeCode` through a co-located `AccumulatorEntry.StateType` (one central discriminator — open/closed for new bar types). The batch driver (`AggregationPipeline`) saves its trailing partial; the live driver (`TickAggregationBarSource`) restores at session start via the `IBarSource.Start()` hook. The existing Renko batch-append path migrates onto the new seam and the old `long` seam is deleted.

**Tech Stack:** C# 14 / .NET 10, System.Text.Json, xUnit + NSubstitute, `IFileStorage` (local/S3).

## Global Constraints

- **C# 14 / .NET 10.** One `dotnet` process at a time (build/test strictly sequential). Use `powershell.exe`, never `pwsh`.
- **Domain has ZERO ProjectReferences.** `AccumulatorState`, `AccumulatorEntry`, and `AccumulatorStateJson` live in `AlgoTradeForge.Domain` and may use only BCL types (System.Text.Json is BCL — allowed).
- **Open/closed for new bar types:** the ONLY central discriminator is `typeCode`, dispatched in `AccumulatorEntry`. Adding a bar type = its accumulator + its `XxxBarState` record + a `StateType` case (+ the existing `Open` case). NEVER introduce a second registry (no `[JsonDerivedType]` list, no parallel switch).
- **`Int128` has no JSON representation** — every `Int128` field is persisted as an invariant-culture decimal string (`value.ToString(CultureInfo.InvariantCulture)` / `Int128.Parse(s, CultureInfo.InvariantCulture)`).
- **Persist correctness state only**, never telemetry (`_barsEmitted`, `_overshootSum`, `_maxOvershoot`, `_lastSidecarRow`/`_hasLastSidecar` are NOT saved — they reset per session).
- **No `Async` suffix** on new async methods. **using-over-try/finally.** **One type per file** (each `AccumulatorState` derived record in its own file).
- **Int64 money:** never raw `(long)` casts on monetary values; this plan moves existing `long` fields verbatim, so no new scaling — but `Int128`↔string is the only numeric conversion introduced.
- Commits: implementer does NOT `git add`/commit (hook-denied); the controller stages + commits per task after review. Steps below show the intended commit message; the controller runs it.

---

### Task 1: `AccumulatorState` records + generalized seam + base-family (EqV/EqT/EqD) save/restore

**Files:**
- Create: `src/AlgoTradeForge.Domain/Aggregation/AccumulatorState.cs`
- Create: `src/AlgoTradeForge.Domain/Aggregation/VolumeBarState.cs`
- Create: `src/AlgoTradeForge.Domain/Aggregation/ImbalanceBarState.cs`
- Create: `src/AlgoTradeForge.Domain/Aggregation/RangeBarState.cs`
- Create: `src/AlgoTradeForge.Domain/Aggregation/RenkoBarState.cs`
- Modify: `src/AlgoTradeForge.Domain/Aggregation/IBarAccumulator.cs` (add new seam methods as defaults; LEAVE the old Renko `SeedResumeState`/`TryGetResumeState` in place for now — removed in Task 7)
- Modify: `src/AlgoTradeForge.Domain/Aggregation/Accumulators/AccumulatorBase.cs` (implement save/restore once for EqV/EqT/EqD)
- Test: `tests/AlgoTradeForge.Domain.Tests/Aggregation/Accumulators/AccumulatorStateRoundTripTests.cs`

**Interfaces:**
- Produces:
  - `public abstract record AccumulatorState;` (marker base)
  - `public sealed record VolumeBarState(long TsOpen, long Open, long High, long Low, long Close, string ThresholdAcc, long BaseVolume) : AccumulatorState;` — `ThresholdAcc` is `Int128` as invariant string.
  - `public sealed record ImbalanceBarState(long TsOpen, long Open, long High, long Low, long Close, string SignedAcc, string BuyAcc, string SellAcc, long BaseVolume) : AccumulatorState;` — the three `*Acc` are `Int128` strings (EqID is natively `Int128`; EqIV/EqIT widen their `long` losslessly).
  - `public sealed record RangeBarState(long TsOpen, long Open, long RunningHigh, long RunningLow, long Close, long BaseVolume) : AccumulatorState;`
  - `public sealed record RenkoBarState(bool Seeded, long LastBrickClose, long PendingVolume, long LastEmittedTs) : AccumulatorState;`
  - On `IBarAccumulator`: `bool TrySaveState(out AccumulatorState? state)` (default `state = null; return false;`) and `void RestoreState(AccumulatorState state)` (default no-op).
  - A restored Volume/Imbalance/Range accumulator has an in-progress bar (`_barEmpty = false`); a saved state is only produced when a partial bar exists (so `TrySaveState` returns `false` on an empty bar).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.Domain.Tests/Aggregation/Accumulators/AccumulatorStateRoundTripTests.cs
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation.Accumulators;

public class AccumulatorStateRoundTripTests
{
    private static ScaleContext Scale() => new(tickSize: 0.01m);

    private static SourceRecord Rec(long ts, long price, long qty) =>
        new(ts, price, price, price, price, qty);

    // Feeding the same sub-threshold prefix to a fresh accumulator and to a save->restore'd
    // accumulator must yield identical emission behaviour on the next tick.
    [Fact]
    public void EqV_save_restore_continues_partial_bar()
    {
        // EqV threshold 40; feed 30 units (no emit), save mid-bar, restore into a fresh acc.
        var a = AccumulatorEntry.Open("EqV", threshold: 40, Scale(), Scale(), DataFeedKind.Tick);
        a.TryAdvance(Rec(1000, 5_000_000, 30), out _);

        Assert.True(a.TrySaveState(out var state));
        Assert.IsType<VolumeBarState>(state);

        var b = AccumulatorEntry.Open("EqV", threshold: 40, Scale(), Scale(), DataFeedKind.Tick);
        b.RestoreState(state!);

        // 15 more units crosses 40 -> both emit on this tick with identical bars.
        var aEmit = a.TryAdvance(Rec(1001, 5_000_100, 15), out var aBar);
        var bEmit = b.TryAdvance(Rec(1001, 5_000_100, 15), out var bBar);
        Assert.True(aEmit);
        Assert.Equal(aEmit, bEmit);
        Assert.Equal(aBar, bBar); // record-struct equality, element-wise
    }

    [Fact]
    public void TrySaveState_on_empty_bar_returns_false()
    {
        var a = AccumulatorEntry.Open("EqV", threshold: 40, Scale(), Scale(), DataFeedKind.Tick);
        Assert.False(a.TrySaveState(out var state));
        Assert.Null(state);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateRoundTripTests`
Expected: FAIL — `TrySaveState`/`RestoreState`/`VolumeBarState` do not exist (compile error).

- [ ] **Step 3: Create the state records**

```csharp
// src/AlgoTradeForge.Domain/Aggregation/AccumulatorState.cs
namespace AlgoTradeForge.Domain.Aggregation;

/// <summary>
/// Persisted mid-bar (partial) state of an <see cref="IBarAccumulator"/>. One derived record
/// per accumulator family; the concrete type is resolved by type-code via
/// <see cref="AccumulatorEntry.StateType"/>. Correctness state only — never telemetry.
/// </summary>
public abstract record AccumulatorState;
```

```csharp
// src/AlgoTradeForge.Domain/Aggregation/VolumeBarState.cs
namespace AlgoTradeForge.Domain.Aggregation;

// EqV / EqT / EqD. ThresholdAcc is the Int128 _thresholdAcc as an invariant decimal string.
public sealed record VolumeBarState(
    long TsOpen, long Open, long High, long Low, long Close, string ThresholdAcc, long BaseVolume)
    : AccumulatorState;
```

```csharp
// src/AlgoTradeForge.Domain/Aggregation/ImbalanceBarState.cs
namespace AlgoTradeForge.Domain.Aggregation;

// EqIV / EqID / EqIT. Signed/Buy/Sell are Int128 invariant strings: EqID is natively Int128;
// EqIV/EqIT widen their long accumulators losslessly so all three share one record shape.
public sealed record ImbalanceBarState(
    long TsOpen, long Open, long High, long Low, long Close,
    string SignedAcc, string BuyAcc, string SellAcc, long BaseVolume)
    : AccumulatorState;
```

```csharp
// src/AlgoTradeForge.Domain/Aggregation/RangeBarState.cs
namespace AlgoTradeForge.Domain.Aggregation;

public sealed record RangeBarState(
    long TsOpen, long Open, long RunningHigh, long RunningLow, long Close, long BaseVolume)
    : AccumulatorState;
```

```csharp
// src/AlgoTradeForge.Domain/Aggregation/RenkoBarState.cs
namespace AlgoTradeForge.Domain.Aggregation;

// Renko has no "partial bar" — its state is the inter-brick wall + pending volume + last ts.
public sealed record RenkoBarState(
    bool Seeded, long LastBrickClose, long PendingVolume, long LastEmittedTs)
    : AccumulatorState;
```

- [ ] **Step 4: Add the seam to `IBarAccumulator`**

In `src/AlgoTradeForge.Domain/Aggregation/IBarAccumulator.cs`, add these default members to the interface (immediately after the existing `TryGetResumeState` line — keep the old Renko methods for now):

```csharp
    /// <summary>
    /// Capture the in-progress (mid-bar) state for persistence. Returns <c>false</c> with a
    /// null state when there is nothing worth seeding (e.g. an empty bar). Generalizes the
    /// Renko-only resume seam to every family.
    /// </summary>
    bool TrySaveState(out AccumulatorState? state) { state = null; return false; }

    /// <summary>
    /// Re-seed from a previously saved state so the next <see cref="TryAdvance"/> continues the
    /// in-progress bar. Throws if the state's concrete type does not match this accumulator.
    /// </summary>
    void RestoreState(AccumulatorState state) { }
```

- [ ] **Step 5: Implement save/restore in `AccumulatorBase`**

In `src/AlgoTradeForge.Domain/Aggregation/Accumulators/AccumulatorBase.cs`, add `using System.Globalization;` at the top, and these methods to the class body:

```csharp
    public bool TrySaveState(out AccumulatorState? state)
    {
        if (_barEmpty) { state = null; return false; }
        state = new VolumeBarState(
            _tsOpen, _open, _high, _low, _close,
            _thresholdAcc.ToString(CultureInfo.InvariantCulture), _baseVolumeAcc);
        return true;
    }

    public void RestoreState(AccumulatorState state)
    {
        if (state is not VolumeBarState v)
            throw new ArgumentException(
                $"Expected {nameof(VolumeBarState)} for an EqV/EqT/EqD accumulator, got {state.GetType().Name}.",
                nameof(state));
        _barEmpty = false;
        _tsOpen = v.TsOpen; _open = v.Open; _high = v.High; _low = v.Low; _close = v.Close;
        _thresholdAcc = Int128.Parse(v.ThresholdAcc, CultureInfo.InvariantCulture);
        _baseVolumeAcc = v.BaseVolume;
    }
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateRoundTripTests`
Expected: PASS (both tests).

- [ ] **Step 7: Build the full solution**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: 0 errors (old Renko seam untouched, new defaults additive).

- [ ] **Step 8: Commit**

```bash
git add src/AlgoTradeForge.Domain/Aggregation/ tests/AlgoTradeForge.Domain.Tests/Aggregation/Accumulators/AccumulatorStateRoundTripTests.cs
git commit -F - <<'EOF'
feat(domain): generalized accumulator resume seam + EqV/EqT/EqD save-restore (Plan 0)
EOF
```

---

### Task 2: Imbalance-family (EqIV/EqID/EqIT) save/restore

**Files:**
- Modify: `src/AlgoTradeForge.Domain/Aggregation/Accumulators/EqIVAccumulator.cs`
- Modify: `src/AlgoTradeForge.Domain/Aggregation/Accumulators/EqIDAccumulator.cs`
- Modify: `src/AlgoTradeForge.Domain/Aggregation/Accumulators/EqITAccumulator.cs`
- Test: `tests/AlgoTradeForge.Domain.Tests/Aggregation/Accumulators/AccumulatorStateRoundTripTests.cs` (add cases)

**Interfaces:**
- Consumes: `ImbalanceBarState` (Task 1).
- Produces: `TrySaveState`/`RestoreState` on all three imbalance accumulators, each producing/consuming an `ImbalanceBarState`.

- [ ] **Step 1: Add failing tests for all three imbalance families**

Append to `AccumulatorStateRoundTripTests.cs`:

```csharp
    [Theory]
    [InlineData("EqIV", 20)]
    [InlineData("EqID", 20_000_000)]
    [InlineData("EqIT", 5)]
    public void Imbalance_save_restore_continues_partial_bar(string typeCode, long threshold)
    {
        // Buy-biased records accumulate signed imbalance without crossing on the first few.
        SourceRecord Buy(long ts, long price, long qty) =>
            new(ts, price, price, price, price, qty, BuyVolumeLong: qty, SellVolumeLong: 0);

        var a = AccumulatorEntry.Open(typeCode, threshold, Scale(), Scale(), DataFeedKind.Tick);
        a.TryAdvance(Buy(1000, 5_000_000, 3), out _);
        a.TryAdvance(Buy(1001, 5_000_000, 3), out _);

        Assert.True(a.TrySaveState(out var state));
        Assert.IsType<ImbalanceBarState>(state);

        var b = AccumulatorEntry.Open(typeCode, threshold, Scale(), Scale(), DataFeedKind.Tick);
        b.RestoreState(state!);

        // Drive both to a cross with an identical record stream and compare every emission.
        var aBars = new List<AggregatedBar>();
        var bBars = new List<AggregatedBar>();
        for (long i = 0; i < 200; i++)
        {
            var r = Buy(1100 + i, 5_000_000 + i, 3);
            if (a.TryAdvance(in r, out var ab)) aBars.Add(ab);
            if (b.TryAdvance(in r, out var bb)) bBars.Add(bb);
        }
        Assert.NotEmpty(aBars);
        Assert.Equal(aBars, bBars);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateRoundTripTests`
Expected: FAIL — imbalance accumulators inherit the default `TrySaveState` (returns false), so `Assert.True(a.TrySaveState(...))` fails.

- [ ] **Step 3: Implement in `EqIVAccumulator` (long accumulators → Int128 string)**

Add `using System.Globalization;` and to the class body:

```csharp
    public bool TrySaveState(out AccumulatorState? state)
    {
        if (_barEmpty) { state = null; return false; }
        state = new ImbalanceBarState(
            _tsOpen, _open, _high, _low, _close,
            ((Int128)_signedAccLong).ToString(CultureInfo.InvariantCulture),
            ((Int128)_buyAccLong).ToString(CultureInfo.InvariantCulture),
            ((Int128)_sellAccLong).ToString(CultureInfo.InvariantCulture),
            _baseVolumeAcc);
        return true;
    }

    public void RestoreState(AccumulatorState state)
    {
        if (state is not ImbalanceBarState s)
            throw new ArgumentException($"Expected {nameof(ImbalanceBarState)}, got {state.GetType().Name}.", nameof(state));
        _barEmpty = false;
        _tsOpen = s.TsOpen; _open = s.Open; _high = s.High; _low = s.Low; _close = s.Close;
        _signedAccLong = (long)Int128.Parse(s.SignedAcc, CultureInfo.InvariantCulture);
        _buyAccLong = (long)Int128.Parse(s.BuyAcc, CultureInfo.InvariantCulture);
        _sellAccLong = (long)Int128.Parse(s.SellAcc, CultureInfo.InvariantCulture);
        _baseVolumeAcc = s.BaseVolume;
    }
```

- [ ] **Step 4: Implement in `EqIDAccumulator` (native Int128 accumulators)**

Add `using System.Globalization;` and to the class body:

```csharp
    public bool TrySaveState(out AccumulatorState? state)
    {
        if (_barEmpty) { state = null; return false; }
        state = new ImbalanceBarState(
            _tsOpen, _open, _high, _low, _close,
            _signedDollarTickAcc.ToString(CultureInfo.InvariantCulture),
            _buyDollarTickAcc.ToString(CultureInfo.InvariantCulture),
            _sellDollarTickAcc.ToString(CultureInfo.InvariantCulture),
            _baseVolumeAcc);
        return true;
    }

    public void RestoreState(AccumulatorState state)
    {
        if (state is not ImbalanceBarState s)
            throw new ArgumentException($"Expected {nameof(ImbalanceBarState)}, got {state.GetType().Name}.", nameof(state));
        _barEmpty = false;
        _tsOpen = s.TsOpen; _open = s.Open; _high = s.High; _low = s.Low; _close = s.Close;
        _signedDollarTickAcc = Int128.Parse(s.SignedAcc, CultureInfo.InvariantCulture);
        _buyDollarTickAcc = Int128.Parse(s.BuyAcc, CultureInfo.InvariantCulture);
        _sellDollarTickAcc = Int128.Parse(s.SellAcc, CultureInfo.InvariantCulture);
        _baseVolumeAcc = s.BaseVolume;
    }
```

- [ ] **Step 5: Implement in `EqITAccumulator` (long count accumulators → Int128 string)**

Add `using System.Globalization;` and the SAME body as `EqIVAccumulator` Step 3, but mapping the count fields:

```csharp
    public bool TrySaveState(out AccumulatorState? state)
    {
        if (_barEmpty) { state = null; return false; }
        state = new ImbalanceBarState(
            _tsOpen, _open, _high, _low, _close,
            ((Int128)_signedCountAcc).ToString(CultureInfo.InvariantCulture),
            ((Int128)_buyCountAcc).ToString(CultureInfo.InvariantCulture),
            ((Int128)_sellCountAcc).ToString(CultureInfo.InvariantCulture),
            _baseVolumeAcc);
        return true;
    }

    public void RestoreState(AccumulatorState state)
    {
        if (state is not ImbalanceBarState s)
            throw new ArgumentException($"Expected {nameof(ImbalanceBarState)}, got {state.GetType().Name}.", nameof(state));
        _barEmpty = false;
        _tsOpen = s.TsOpen; _open = s.Open; _high = s.High; _low = s.Low; _close = s.Close;
        _signedCountAcc = (long)Int128.Parse(s.SignedAcc, CultureInfo.InvariantCulture);
        _buyCountAcc = (long)Int128.Parse(s.BuyAcc, CultureInfo.InvariantCulture);
        _sellCountAcc = (long)Int128.Parse(s.SellAcc, CultureInfo.InvariantCulture);
        _baseVolumeAcc = s.BaseVolume;
    }
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateRoundTripTests`
Expected: PASS (EqIV/EqID/EqIT cases included).

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.Domain/Aggregation/Accumulators/EqIVAccumulator.cs src/AlgoTradeForge.Domain/Aggregation/Accumulators/EqIDAccumulator.cs src/AlgoTradeForge.Domain/Aggregation/Accumulators/EqITAccumulator.cs tests/AlgoTradeForge.Domain.Tests/Aggregation/Accumulators/AccumulatorStateRoundTripTests.cs
git commit -F - <<'EOF'
feat(domain): imbalance-family accumulator save-restore (Plan 0)
EOF
```

---

### Task 3: Range save/restore

**Files:**
- Modify: `src/AlgoTradeForge.Domain/Aggregation/Accumulators/RangeAccumulator.cs`
- Test: `AccumulatorStateRoundTripTests.cs` (add a Range case)

**Interfaces:**
- Consumes: `RangeBarState` (Task 1).

- [ ] **Step 1: Add failing test**

```csharp
    [Fact]
    public void Range_save_restore_continues_partial_bar()
    {
        // Range threshold 60 price-ticks; build a 40-tick span (no emit), save, restore.
        SourceRecord R(long ts, long h, long l) => new(ts, h, h, l, l, 1);
        var a = AccumulatorEntry.Open("Range", threshold: 60, Scale(), Scale(), DataFeedKind.Tick);
        a.TryAdvance(R(1000, 5_000_000, 5_000_000), out _);
        a.TryAdvance(R(1001, 5_000_040, 5_000_000), out _); // running range 40

        Assert.True(a.TrySaveState(out var state));
        Assert.IsType<RangeBarState>(state);

        var b = AccumulatorEntry.Open("Range", threshold: 60, Scale(), Scale(), DataFeedKind.Tick);
        b.RestoreState(state!);

        var aEmit = a.TryAdvance(R(1002, 5_000_061, 5_000_000), out var aBar); // range 61 -> emit
        var bEmit = b.TryAdvance(R(1002, 5_000_061, 5_000_000), out var bBar);
        Assert.True(aEmit);
        Assert.Equal(aEmit, bEmit);
        Assert.Equal(aBar, bBar);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateRoundTripTests`
Expected: FAIL — `Assert.True(a.TrySaveState(...))` fails (default impl returns false).

- [ ] **Step 3: Implement in `RangeAccumulator`**

```csharp
    public bool TrySaveState(out AccumulatorState? state)
    {
        if (_barEmpty) { state = null; return false; }
        state = new RangeBarState(_tsOpen, _open, _runningHigh, _runningLow, _close, _baseVolumeAcc);
        return true;
    }

    public void RestoreState(AccumulatorState state)
    {
        if (state is not RangeBarState s)
            throw new ArgumentException($"Expected {nameof(RangeBarState)}, got {state.GetType().Name}.", nameof(state));
        _barEmpty = false;
        _tsOpen = s.TsOpen; _open = s.Open; _runningHigh = s.RunningHigh; _runningLow = s.RunningLow;
        _close = s.Close; _baseVolumeAcc = s.BaseVolume;
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateRoundTripTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.Domain/Aggregation/Accumulators/RangeAccumulator.cs tests/AlgoTradeForge.Domain.Tests/Aggregation/Accumulators/AccumulatorStateRoundTripTests.cs
git commit -F - <<'EOF'
feat(domain): range accumulator save-restore (Plan 0)
EOF
```

---

### Task 4: Renko save/restore (new seam, alongside the old `long` seam)

**Files:**
- Modify: `src/AlgoTradeForge.Domain/Aggregation/Accumulators/RenkoAccumulator.cs` (ADD the new methods; do NOT remove `SeedResumeState`/`TryGetResumeState` yet — Task 7)
- Test: `AccumulatorStateRoundTripTests.cs` (add a Renko case)

**Interfaces:**
- Consumes: `RenkoBarState` (Task 1).
- Note: Renko's `TrySaveState` ALWAYS returns `true` (its wall + pending volume always matter, even with no partial bar) — unlike the other families, which return `false` on an empty bar. Drain `_queue` is NOT part of saved state: callers drain via `TryDrainQueued` before saving, and a freshly-restored accumulator has an empty queue.

- [ ] **Step 1: Add failing test (carries `_pendingVolume` forward — the gap the old `long` seam couldn't)**

```csharp
    [Fact]
    public void Renko_save_restore_carries_wall_and_pending_volume()
    {
        // brick size 50; seed the anchor, accumulate a sub-brick move so pending volume builds.
        SourceRecord R(long ts, long close, long vol) => new(ts, close, close, close, close, vol);
        var a = AccumulatorEntry.Open("Renko", threshold: 50, Scale(), Scale(), DataFeedKind.Tick);
        a.TryAdvance(R(1000, 5_000_000, 7), out _);  // seeds wall=5_000_000, pending=7
        a.TryAdvance(R(1001, 5_000_020, 4), out _);  // +20 < 50 -> pending=11, no emit

        Assert.True(a.TrySaveState(out var state));
        Assert.IsType<RenkoBarState>(state);

        var b = AccumulatorEntry.Open("Renko", threshold: 50, Scale(), Scale(), DataFeedKind.Tick);
        b.RestoreState(state!);

        // A +50 move emits one brick; its volume must include the carried pending (11) + share.
        var aEmit = a.TryAdvance(R(1002, 5_000_070, 6), out var aBar);
        var bEmit = b.TryAdvance(R(1002, 5_000_070, 6), out var bBar);
        Assert.True(aEmit);
        Assert.Equal(aEmit, bEmit);
        Assert.Equal(aBar, bBar);          // identical brick incl. volume
        Assert.Equal(11 + 6, bBar.Volume); // pending(11) + this tick's single-brick volume(6)
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateRoundTripTests`
Expected: FAIL — `Assert.IsType<RenkoBarState>` fails (Renko still only has the old `long` seam).

- [ ] **Step 3: Add the new seam methods to `RenkoAccumulator`** (leave `SeedResumeState`/`TryGetResumeState` as-is)

```csharp
    public bool TrySaveState(out AccumulatorState? state)
    {
        state = new RenkoBarState(_seeded, _lastBrickClose, _pendingVolume, _lastEmittedTs);
        return true;
    }

    public void RestoreState(AccumulatorState state)
    {
        if (state is not RenkoBarState s)
            throw new ArgumentException($"Expected {nameof(RenkoBarState)}, got {state.GetType().Name}.", nameof(state));
        _seeded = s.Seeded;
        _lastBrickClose = s.LastBrickClose;
        _pendingVolume = s.PendingVolume;
        _lastEmittedTs = s.LastEmittedTs;
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateRoundTripTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.Domain/Aggregation/Accumulators/RenkoAccumulator.cs tests/AlgoTradeForge.Domain.Tests/Aggregation/Accumulators/AccumulatorStateRoundTripTests.cs
git commit -F - <<'EOF'
feat(domain): renko accumulator full-state save-restore (Plan 0)
EOF
```

---

### Task 5: `AccumulatorEntry.StateType` + `AccumulatorStateJson` codec

**Files:**
- Modify: `src/AlgoTradeForge.Domain/Aggregation/AccumulatorEntry.cs` (add `StateType`)
- Create: `src/AlgoTradeForge.Domain/Aggregation/AccumulatorStateJson.cs`
- Test: `tests/AlgoTradeForge.Domain.Tests/Aggregation/AccumulatorStateJsonTests.cs`

**Interfaces:**
- Produces:
  - `public static Type AccumulatorEntry.StateType(string typeCode)` — the concrete `AccumulatorState` type for a type-code; throws on unknown.
  - `public static string AccumulatorStateJson.Serialize(string typeCode, AccumulatorState state)` → `{"typeCode":"…","state":{…}}`.
  - `public static (string TypeCode, AccumulatorState State) AccumulatorStateJson.Deserialize(string json)`.

- [ ] **Step 1: Write the failing test (every family round-trips, incl. Int128 strings)**

```csharp
// tests/AlgoTradeForge.Domain.Tests/Aggregation/AccumulatorStateJsonTests.cs
using AlgoTradeForge.Domain.Aggregation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation;

public class AccumulatorStateJsonTests
{
    public static IEnumerable<object[]> Cases() => new List<object[]>
    {
        new object[] { "EqV", (AccumulatorState)new VolumeBarState(1, 2, 3, 4, 5, "170141183460469231731687303715884105727", 6) }, // Int128.MaxValue
        new object[] { "EqID", (AccumulatorState)new ImbalanceBarState(1, 2, 3, 4, 5, "-12345678901234567890", "999", "1", 6) },
        new object[] { "Range", (AccumulatorState)new RangeBarState(1, 2, 3, 4, 5, 6) },
        new object[] { "Renko", (AccumulatorState)new RenkoBarState(true, 100, 7, 1234) },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Round_trips_through_typecode_discriminated_json(string typeCode, AccumulatorState state)
    {
        var json = AccumulatorStateJson.Serialize(typeCode, state);
        var (gotTypeCode, gotState) = AccumulatorStateJson.Deserialize(json);
        Assert.Equal(typeCode, gotTypeCode);
        Assert.Equal(state, gotState); // record equality
    }

    [Fact]
    public void StateType_throws_on_unknown_typecode()
    {
        Assert.Throws<ArgumentException>(() => AccumulatorEntry.StateType("Nope"));
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateJsonTests`
Expected: FAIL — `StateType`/`AccumulatorStateJson` do not exist.

- [ ] **Step 3: Add `StateType` to `AccumulatorEntry`**

```csharp
    /// <summary>
    /// Concrete <see cref="AccumulatorState"/> type for a type-code. Co-located with
    /// <see cref="Open"/> so a new bar type registers its state shape in the SAME place it
    /// registers its accumulator — one central discriminator, never two.
    /// </summary>
    public static Type StateType(string typeCode)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeCode);
        return typeCode switch
        {
            "EqV" or "EqT" or "EqD" => typeof(VolumeBarState),
            "EqIV" or "EqID" or "EqIT" => typeof(ImbalanceBarState),
            "Range" => typeof(RangeBarState),
            "Renko" => typeof(RenkoBarState),
            _ => throw new ArgumentException(
                $"No AccumulatorState type for '{typeCode}' (allowed: EqT, EqV, EqD, EqIV, EqID, EqIT, Range, Renko).",
                nameof(typeCode)),
        };
    }
```

- [ ] **Step 4: Create the codec**

```csharp
// src/AlgoTradeForge.Domain/Aggregation/AccumulatorStateJson.cs
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlgoTradeForge.Domain.Aggregation;

/// <summary>
/// Maps an <see cref="AccumulatorState"/> to/from a type-code-discriminated JSON envelope
/// (<c>{"typeCode":"…","state":{…}}</c>). The concrete state type is resolved by type-code via
/// <see cref="AccumulatorEntry.StateType"/>, so the <c>state</c> object is serialized as a plain
/// non-polymorphic record — no JsonDerivedType registry. Domain-resident because it is shared by
/// the batch (HistoryLoader) and live (LiveHost) hosts, which must not reference each other.
/// </summary>
public static class AccumulatorStateJson
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    public static string Serialize(string typeCode, AccumulatorState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeCode);
        ArgumentNullException.ThrowIfNull(state);
        var stateType = AccumulatorEntry.StateType(typeCode);
        var envelope = new JsonObject
        {
            ["typeCode"] = typeCode,
            ["state"] = JsonSerializer.SerializeToNode(state, stateType, Opts),
        };
        return envelope.ToJsonString(Opts);
    }

    public static (string TypeCode, AccumulatorState State) Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        var envelope = JsonNode.Parse(json)!.AsObject();
        var typeCode = (string)envelope["typeCode"]!;
        var stateType = AccumulatorEntry.StateType(typeCode);
        var state = (AccumulatorState)JsonSerializer.Deserialize(envelope["state"], stateType, Opts)!;
        return (typeCode, state);
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter AccumulatorStateJsonTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.Domain/Aggregation/AccumulatorEntry.cs src/AlgoTradeForge.Domain/Aggregation/AccumulatorStateJson.cs tests/AlgoTradeForge.Domain.Tests/Aggregation/AccumulatorStateJsonTests.cs
git commit -F - <<'EOF'
feat(domain): typeCode-discriminated AccumulatorState codec (open/closed) (Plan 0)
EOF
```

---

### Task 6: Golden — batch ≡ live across a mid-bar restart (THE M6 proof)

**Files:**
- Modify: `tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/BatchEqualsLiveGoldenTests.cs`

**Interfaces:**
- Consumes: `TrySaveState`/`RestoreState` (Tasks 1–4), `AccumulatorStateJson` (Task 5), the existing `SyntheticTicks`/`ThresholdFor`/`RunBatch`/`ToInt64Bar` helpers in this file.

This task proves the M6 golden property **across a save→serialize→deserialize→restore boundary**: a single-pass batch run must equal a run that is interrupted mid-stream, persisted through the codec, and resumed — for all 8 families. No new persistence wiring; it exercises the seam + codec directly.

- [ ] **Step 1: Add the failing restart-golden test**

```csharp
    [Theory]
    [InlineData("EqV")]
    [InlineData("EqT")]
    [InlineData("EqD")]
    [InlineData("EqIV")]
    [InlineData("EqID")]
    [InlineData("EqIT")]
    [InlineData("Range")]
    [InlineData("Renko")]
    public void Resumed_run_through_codec_equals_single_pass(string typeCode)
    {
        var ticks = SyntheticTicks(count: 4000);
        var threshold = ThresholdFor(typeCode);
        var scale = Scale();

        // Reference: single uninterrupted pass.
        var reference = RunBatch(typeCode, threshold, scale, ticks);

        // Interrupted: feed the first half, snapshot through the codec, resume into a fresh acc.
        var split = ticks.Count / 2;
        var resumed = new List<Int64Bar>();

        var first = AccumulatorEntry.Open(typeCode, threshold, scale, scale, DataFeedKind.Tick);
        for (var i = 0; i < split; i++)
        {
            var rec = TickToSourceRecord.From(ticks[i]);
            if (first.TryAdvance(in rec, out var bar)) resumed.Add(ToInt64Bar(in bar));
            while (first.TryDrainQueued(out var extra)) resumed.Add(ToInt64Bar(in extra));
        }

        AccumulatorState? snapshot = first.TrySaveState(out var s) ? s : null;

        var second = AccumulatorEntry.Open(typeCode, threshold, scale, scale, DataFeedKind.Tick);
        if (snapshot is not null)
        {
            // Round-trip through the persisted form, not just the in-memory object.
            var json = AccumulatorStateJson.Serialize(typeCode, snapshot);
            var (_, restored) = AccumulatorStateJson.Deserialize(json);
            second.RestoreState(restored);
        }

        for (var i = split; i < ticks.Count; i++)
        {
            var rec = TickToSourceRecord.From(ticks[i]);
            if (second.TryAdvance(in rec, out var bar)) resumed.Add(ToInt64Bar(in bar));
            while (second.TryDrainQueued(out var extra)) resumed.Add(ToInt64Bar(in extra));
        }

        Assert.True(reference.Count > 0, $"{typeCode}: reference produced no bars — test vacuous.");
        Assert.Equal(reference, resumed); // element-wise Int64Bar equality across the restart
    }
```

Add `using AlgoTradeForge.Domain.Aggregation;` if not already present (it is — `AccumulatorEntry`/`AccumulatorState` are in that namespace).

- [ ] **Step 2: Run to verify it passes (the seam is already implemented)**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter BatchEqualsLiveGoldenTests`
Expected: PASS for all 8 families. If any family fails, the bug is in that family's save/restore (Tasks 1–4) — fix there, do not weaken the assertion.

- [ ] **Step 3: Commit**

```bash
git add tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/BatchEqualsLiveGoldenTests.cs
git commit -F - <<'EOF'
test(livehost): M6 golden — batch≡live across a codec-roundtrip mid-bar restart (Plan 0)
EOF
```

---

### Task 7: Migrate the Renko batch-append onto the new seam; delete the old `long` seam

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/AggregationPipeline.cs:84-92` (seed) and `:372-374` (read-back)
- Modify: `src/AlgoTradeForge.Domain/Aggregation/IBarAccumulator.cs` (DELETE `SeedResumeState`/`TryGetResumeState`)
- Modify: `src/AlgoTradeForge.Domain/Aggregation/Accumulators/RenkoAccumulator.cs` (DELETE its `SeedResumeState`/`TryGetResumeState` overrides)
- Modify: `tests/AlgoTradeForge.Domain.Tests/Aggregation/Accumulators/RenkoAccumulatorTests.cs` (update any test calling the old seam to use `RestoreState(new RenkoBarState(...))` / `TrySaveState`)

**Interfaces:**
- The manifest field `Build.LastBrickClose` is UNCHANGED (alt-bar feed format frozen); it is now populated from a `RenkoBarState` instead of the old `out long`.
- Batch-append seeding constructs `new RenkoBarState(Seeded: true, LastBrickClose: anchor, PendingVolume: 0, LastEmittedTs: 0)` — preserving the old partition-boundary semantics (pending volume discarded at the boundary).

- [ ] **Step 1: Update the seed site (`AggregationPipeline` ~line 89)**

Replace:

```csharp
        if (job.Resume is { LastBrickClose: { } anchor })
        {
            accumulator.SeedResumeState(anchor);
        }
```

with:

```csharp
        // Renko batch-append: re-seed only the inter-brick wall from the manifest. Pending volume
        // is intentionally zero at a partition boundary (the prior run discarded its trailing
        // pending at Complete). Distinct from M6 mid-bar live seeding, which carries pending.
        if (job.Resume is { LastBrickClose: { } anchor })
        {
            accumulator.RestoreState(new RenkoBarState(
                Seeded: true, LastBrickClose: anchor, PendingVolume: 0, LastEmittedTs: 0));
        }
```

- [ ] **Step 2: Update the read-back site (`AggregationPipeline` ~line 372)**

Replace:

```csharp
        long? lastBrickClose = null;
        if (accumulator.TryGetResumeState(out var resumeClose))
            lastBrickClose = resumeClose;
```

with:

```csharp
        // Persist Renko's wall to the manifest for the next append. Non-Renko accumulators
        // produce a non-Renko (or no) state and leave LastBrickClose null.
        long? lastBrickClose = null;
        if (accumulator.TrySaveState(out var endState) && endState is RenkoBarState renko)
            lastBrickClose = renko.LastBrickClose;
```

Add `using AlgoTradeForge.Domain.Aggregation;` to `AggregationPipeline.cs` if `RenkoBarState` is not already in scope (the file already uses `AlgoTradeForge.Domain.Aggregation` — confirm the using is present).

- [ ] **Step 3: Delete the old seam from `IBarAccumulator`**

Remove these two members (and their comment) entirely:

```csharp
    // Renko resume seam: persist/restore the last brick close across partition boundaries.
    void SeedResumeState(long lastBrickClose) { }
    bool TryGetResumeState(out long lastBrickClose) { lastBrickClose = 0L; return false; }
```

- [ ] **Step 4: Delete the old overrides from `RenkoAccumulator`**

Remove `SeedResumeState(long)` and `TryGetResumeState(out long)` (the new `TrySaveState`/`RestoreState` from Task 4 replace them). The `_seeded`/`_lastBrickClose`/`_pendingVolume`/`_lastEmittedTs` fields stay.

- [ ] **Step 5: Fix any test references**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter RenkoAccumulator`
If it fails to COMPILE because a test calls `SeedResumeState`/`TryGetResumeState`, update that test to the new seam:
- `acc.SeedResumeState(x)` → `acc.RestoreState(new RenkoBarState(true, x, 0, 0))`
- `acc.TryGetResumeState(out var c)` → `acc.TrySaveState(out var st); var c = ((RenkoBarState)st!).LastBrickClose;`

- [ ] **Step 6: Run the affected suites**

Run: `dotnet test tests/AlgoTradeForge.Domain.Tests/`
Then: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: PASS — Renko append behavior unchanged; old seam gone.

- [ ] **Step 7: Build the full solution**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: 0 errors (no remaining references to the deleted methods anywhere).

- [ ] **Step 8: Commit**

```bash
git add src/AlgoTradeForge.Domain/Aggregation/IBarAccumulator.cs src/AlgoTradeForge.Domain/Aggregation/Accumulators/RenkoAccumulator.cs src/AlgoTradeForge.HistoryLoader.Application/Aggregation/AggregationPipeline.cs tests/AlgoTradeForge.Domain.Tests/Aggregation/Accumulators/RenkoAccumulatorTests.cs
git commit -F - <<'EOF'
refactor(domain): migrate Renko batch-append to the general seam; delete long resume seam (Plan 0)
EOF
```

---

### Task 8: `IPartialBarStateStore` + `IFileStorage`-backed implementation

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/IPartialBarStateStore.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/PartialBarStateKey.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/PartialBarStateStore.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/PartialBarStateStoreTests.cs`

**Interfaces:**
- Produces:
  - `public interface IPartialBarStateStore { Task<AccumulatorState?> Load(string key, CancellationToken ct = default); Task Save(string key, string typeCode, AccumulatorState state, CancellationToken ct = default); }`
  - `public static class PartialBarStateKey { public static string For(string assetOrInstrument, string feedId); }` → `"{assetOrInstrument}/aggregated/{feedId}/_partial-bar-state.json"`.
  - `public sealed class PartialBarStateStore(IFileStorage storage) : IPartialBarStateStore`.
- Single-writer per key (a live session, or one batch job) — atomic replace (`WriteAllText`), no CAS needed.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/PartialBarStateStoreTests.cs
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.DataPlane;

public class PartialBarStateStoreTests
{
    [Fact]
    public async Task Load_returns_null_when_absent()
    {
        using var tmp = new TempDataRoot();
        var store = new PartialBarStateStore(tmp.Storage);
        Assert.Null(await store.Load(PartialBarStateKey.For("BTCUSDT_perp", "EqV-100")));
    }

    [Fact]
    public async Task Save_then_load_round_trips_state()
    {
        using var tmp = new TempDataRoot();
        var store = new PartialBarStateStore(tmp.Storage);
        var key = PartialBarStateKey.For("BTCUSDT_perp", "EqV-100");
        var state = new VolumeBarState(1, 2, 3, 4, 5, "42", 6);

        await store.Save(key, "EqV", state);
        var loaded = await store.Load(key);

        Assert.Equal(state, loaded);
    }
}
```

NOTE: `TempDataRoot` is a test helper that wraps a `LocalFileStorage` over a temp dir. If one does not already exist in this test project, create it:

```csharp
// tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/TempDataRoot.cs
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.DataPlane;

internal sealed class TempDataRoot : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "atf-partial-" + Guid.NewGuid().ToString("N"));
    public IFileStorage Storage { get; }

    public TempDataRoot()
    {
        Directory.CreateDirectory(Root);
        Storage = new LocalFileStorage(Options.Create(new LocalStorageOptions { DataRoot = Root }));
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}
```

Confirm the exact `LocalFileStorage` constructor / `LocalStorageOptions` shape against `src/AlgoTradeForge.Storage/LocalFileStorage.cs` and adjust the helper if the options type differs.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter PartialBarStateStoreTests`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Create the Application-side abstraction + key helper**

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/IPartialBarStateStore.cs
using AlgoTradeForge.Domain.Aggregation;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

/// <summary>
/// Persists/loads an accumulator's mid-bar state (M6 seeding) keyed by an opaque storage key.
/// Single-writer per key, so writes are atomic replace (no CAS).
/// </summary>
public interface IPartialBarStateStore
{
    Task<AccumulatorState?> Load(string key, CancellationToken ct = default);
    Task Save(string key, string typeCode, AccumulatorState state, CancellationToken ct = default);
}
```

```csharp
// src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/PartialBarStateKey.cs
namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public static class PartialBarStateKey
{
    // Lives next to the aggregated alt-bar feed. assetOrInstrument MUST be the same canonical
    // identifier the batch driver uses for the asset dir, so a batch warmup seed and the live
    // session address the same object (batch↔live identifier alignment is finalized in Plan 3).
    public static string For(string assetOrInstrument, string feedId) =>
        $"{assetOrInstrument}/aggregated/{feedId}/_partial-bar-state.json";
}
```

- [ ] **Step 4: Create the Infrastructure implementation**

```csharp
// src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/PartialBarStateStore.cs
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.Storage;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

public sealed class PartialBarStateStore(IFileStorage storage) : IPartialBarStateStore
{
    public async Task<AccumulatorState?> Load(string key, CancellationToken ct = default)
    {
        if (!await storage.Exists(key, ct)) return null;
        var json = await storage.ReadAllText(key, ct);
        var (_, state) = AccumulatorStateJson.Deserialize(json);
        return state;
    }

    public Task Save(string key, string typeCode, AccumulatorState state, CancellationToken ct = default) =>
        storage.WriteAllText(key, AccumulatorStateJson.Serialize(typeCode, state), ct: ct);
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter PartialBarStateStoreTests`
Expected: PASS.

- [ ] **Step 6: Register in DI**

In the LiveHost WebApi composition root (search `AddSingleton<IBarSourceResolver` in `src/AlgoTradeForge.LiveHost.WebApi/`), add:

```csharp
builder.Services.AddSingleton<IPartialBarStateStore, PartialBarStateStore>();
```

Run: `dotnet build AlgoTradeForge.slnx`
Expected: 0 errors.

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/IPartialBarStateStore.cs src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/PartialBarStateKey.cs src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/PartialBarStateStore.cs tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/ src/AlgoTradeForge.LiveHost.WebApi/
git commit -F - <<'EOF'
feat(livehost): IFileStorage-backed partial-bar state store (Plan 0)
EOF
```

---

### Task 9: Live driver — restore at `Start()` + capture for re-persist; resolver wiring

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs`
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/BarSourceResolver.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/TickAggregationBarSourceTests.cs`

**Interfaces:**
- `TickAggregationBarSource` gains an optional final ctor param `Func<CancellationToken, Task<AccumulatorState?>>? loadSeed = null` and overrides `Task Start()` to load + `RestoreState` before any `Feed`.
- `TickAggregationBarSource` gains `public AccumulatorState? CaptureState()` (returns the current partial state, or null) for the session lifecycle to persist on shutdown/rotation.
- `BarSourceResolver` ctor gains `IPartialBarStateStore store`; `ResolveAltBar` passes `loadSeed: ct => store.Load(PartialBarStateKey.For(instrument, ab.FeedId), ct)`.

- [ ] **Step 1: Write the failing test**

```csharp
// add to TickAggregationBarSourceTests.cs
using AlgoTradeForge.Domain.Aggregation;

    [Fact]
    public async Task Start_restores_seed_so_first_live_bar_continues()
    {
        var scale = new ScaleContext(tickSize: 0.01m);

        // Build a partial EqV bar (30 of 40 units) and snapshot it.
        var seedAcc = AccumulatorEntry.Open("EqV", 40, scale, scale, DataFeedKind.Tick);
        var r0 = new SourceRecord(1000, 5_000_000, 5_000_000, 5_000_000, 5_000_000, 30);
        seedAcc.TryAdvance(in r0, out _);
        seedAcc.TrySaveState(out var seed);

        var bars = new List<Int64Bar>();
        var src = new TickAggregationBarSource(
            "EqV", 40, scale, (b, _) => bars.Add(b), recentCapacity: 16,
            loadSeed: _ => Task.FromResult(seed));
        await src.Start();

        // 15 more units crosses 40 because the 30 was seeded; without the seed it would not.
        src.Feed(new TradeTick(1001, 5_000_100, 15, 1, AggressorSide.Buy));

        Assert.Single(bars);
        Assert.Equal(45, bars[0].Volume); // 30 seeded + 15
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter TickAggregationBarSourceTests`
Expected: FAIL — ctor has no `loadSeed` param.

- [ ] **Step 3: Add the seed + capture to `TickAggregationBarSource`**

Add `using AlgoTradeForge.Domain.Aggregation;` (for `AccumulatorState`). Add the field + ctor param + methods:

```csharp
    private readonly Func<CancellationToken, Task<AccumulatorState?>>? _loadSeed;
```

Extend the constructor signature to:

```csharp
    public TickAggregationBarSource(
        string typeCode, long frozenThreshold, ScaleContext scale, Action<Int64Bar, bool> onBar,
        int recentCapacity = 256, Func<CancellationToken, Task<AccumulatorState?>>? loadSeed = null)
```

and at the end of the constructor body add:

```csharp
        _loadSeed = loadSeed;
```

Add the override + capture method:

```csharp
    // Seed the accumulator with persisted mid-bar state before the first tick (M6 parity).
    // The data plane awaits Start() once-on-create before any Feed, so no Feed/restore race.
    public async Task Start()
    {
        if (_loadSeed is null) return;
        var seed = await _loadSeed(CancellationToken.None);
        if (seed is not null) _acc.RestoreState(seed);
    }

    // Current mid-bar state for the session to persist on shutdown/rotation; null if no partial.
    public AccumulatorState? CaptureState() => _acc.TrySaveState(out var s) ? s : null;
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter TickAggregationBarSourceTests`
Expected: PASS.

- [ ] **Step 5: Wire the store into `BarSourceResolver`**

Change the class declaration to inject the store and pass the load thunk:

```csharp
public sealed class BarSourceResolver(BinanceWebSocketManager ws, IPartialBarStateStore stateStore) : IBarSourceResolver
```

Add `using AlgoTradeForge.LiveHost.Application.Live.DataPlane;` (for `IPartialBarStateStore`/`PartialBarStateKey`) — it is already imported. `ResolveAltBar` is currently `static`; make it an instance method and thread `instrument`:

```csharp
            AltBarSubscription ab => ResolveAltBar(instrument, ab, scale, onBar),
```

```csharp
    private TickAggregationBarSource ResolveAltBar(
        string instrument, AltBarSubscription ab, ScaleContext scale, Action<Int64Bar, bool> onBar)
    {
        var feedId = AltBarFeedId.Parse(ab.FeedId);
        var frozenThreshold = ThresholdResolver.ResolveParsed(feedId.TypeCode, feedId.Threshold, scale);
        var key = PartialBarStateKey.For(instrument, ab.FeedId);
        return new TickAggregationBarSource(
            feedId.TypeCode, frozenThreshold, scale, onBar,
            loadSeed: ct => stateStore.Load(key, ct));
    }
```

- [ ] **Step 6: Update `BarSourceResolverTests` construction**

`tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/BarSourceResolverTests.cs` constructs `new BarSourceResolver(ws)`. Update to pass a store — use NSubstitute and stub `Load` to return null (no seed):

```csharp
var store = Substitute.For<IPartialBarStateStore>();
store.Load(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((AccumulatorState?)null);
var resolver = new BarSourceResolver(ws, store);
```

Add `using AlgoTradeForge.Domain.Aggregation;` and `using AlgoTradeForge.LiveHost.Application.Live.DataPlane;` if absent.

- [ ] **Step 7: Run the affected suites + full build**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter BarSourceResolver`
Then: `dotnet build AlgoTradeForge.slnx`
Expected: PASS / 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/DataPlane/TickAggregationBarSource.cs src/AlgoTradeForge.LiveHost.Infrastructure/Live/DataPlane/BarSourceResolver.cs tests/AlgoTradeForge.LiveHost.Application.Tests/DataPlane/TickAggregationBarSourceTests.cs tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/DataPlane/BarSourceResolverTests.cs
git commit -F - <<'EOF'
feat(livehost): live alt-bar source restores mid-bar seed at session start (Plan 0)
EOF
```

---

### Task 10: Batch driver persists its trailing partial

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Aggregation/AggregationPipeline.cs` (after `accumulator.Complete()`, before/around the manifest write)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/AggregationPipelinePartialStateTests.cs` (or extend an existing pipeline test)

**Interfaces:**
- Consumes: `AccumulatorStateJson` (Task 5), the pipeline's existing `_storage` (`IFileStorage`), `feedDir` (`{AssetDir}/aggregated/{OutcomeFeedId}`).
- Writes the trailing partial to `Path.Combine(feedDir, "_partial-bar-state.json")` — the same layout as `PartialBarStateKey.For`, so a live session over the same canonical asset id picks it up as the warmup-tail seed.

- [ ] **Step 1: Write the failing test**

Construct a pipeline job whose source ends mid-bar and assert the partial-state file is written and decodes to the in-progress bar. Mirror the harness of the nearest existing `AggregationPipeline` test in `tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/` (reuse its job/reader/storage setup). The assertion core:

```csharp
        // ...run the pipeline over a source whose tail leaves an in-progress EqV bar...
        var stateKey = Path.Combine(feedDir, "_partial-bar-state.json");
        Assert.True(await storage.Exists(stateKey));
        var (typeCode, state) = AccumulatorStateJson.Deserialize(await storage.ReadAllText(stateKey));
        Assert.Equal("EqV", typeCode);
        Assert.IsType<VolumeBarState>(state);
```

(If standing up a full `AggregationJob` is heavyweight, assert instead at the seam used by the pipeline: feed a known mid-bar tick stream through `AccumulatorEntry.Open` + `TrySaveState` + `AccumulatorStateJson.Serialize`, write via `IFileStorage`, and assert the file decodes — this guards the exact persistence call the pipeline makes.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter AggregationPipelinePartialState`
Expected: FAIL — no partial-state file is written.

- [ ] **Step 3: Persist the trailing partial in `AggregationPipeline.Run`**

Immediately AFTER `var stats = accumulator.Complete();` (and the mono-source `stats with {…}` block), add:

```csharp
        // M6 seeding: persist the trailing in-progress bar so a live session continues it.
        // Complete() does not reset accumulator state, so TrySaveState still reflects the tail.
        if (accumulator.TrySaveState(out var partialState) && partialState is not null)
        {
            var stateKey = Path.Combine(feedDir, "_partial-bar-state.json");
            await _storage.WriteAllText(
                stateKey, AccumulatorStateJson.Serialize(job.TypeCode, partialState), ct: ct);
        }
```

`AccumulatorStateJson` is in `AlgoTradeForge.Domain.Aggregation` (already imported).

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter AggregationPipelinePartialState`
Expected: PASS.

- [ ] **Step 5: Run the full HistoryLoader suite (no regression in append/finalize)**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Aggregation/AggregationPipeline.cs tests/AlgoTradeForge.HistoryLoader.Tests/Aggregation/
git commit -F - <<'EOF'
feat(historyloader): persist trailing partial-bar state for M6 live seeding (Plan 0)
EOF
```

---

### Task 11: Full-solution verification + golden re-run

**Files:** none (verification only).

- [ ] **Step 1: Full build**

Run: `dotnet build AlgoTradeForge.slnx`
Expected: 0 errors / 0 warnings introduced by this plan.

- [ ] **Step 2: Run every touched suite sequentially**

```
dotnet test tests/AlgoTradeForge.Domain.Tests/
dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/
dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/
dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/
```

Expected: all green. The M6 golden (`BatchEqualsLiveGoldenTests`, both the original and the new restart theory) passing for all 8 families is the acceptance gate.

- [ ] **Step 3: Build with the private strategies solution (plugin surface unaffected)**

Run: `dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx`
Expected: 0 errors.

- [ ] **Step 4: Controller checkpoint**

Confirm `git log --oneline` shows the per-task commits and `git status --porcelain` is clean. Whole-plan opus review before merge (M6 golden + Renko-append regression are the critical checks).

---

## Notes / deferred (carried to later plans)

- **Batch-warmup → live-first-session key alignment.** `PartialBarStateKey.For(assetOrInstrument, feedId)` and the batch pipeline's `feedDir` must resolve to the same object for the warmup seed to transfer. Plan 0 wires the **same-host live-restart** path fully (live `CaptureState` → store → live `Start` restore). The cross-host identifier mapping (live instrument ↔ historical asset-dir name) is finalized when Plan 3 builds the live-host instrument addressing. Until then the batch seed transfers only when the two identifiers coincide.
- **Re-persist cadence.** `CaptureState()` exists for the session lifecycle to call on shutdown/rotation; wiring the actual call site (and any periodic timer) into the session stop path is a thin follow-up (the open point in the spec). Not required for the M6 correctness proof.
- **CAS not used.** Partial-bar state has a single writer per key, so atomic `WriteAllText` is correct; `WriteIfMatch` would add no safety. Revisit only if a second concurrent writer per key ever appears.
