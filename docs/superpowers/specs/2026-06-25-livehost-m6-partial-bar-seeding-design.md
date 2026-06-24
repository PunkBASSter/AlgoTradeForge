# LiveHost — M6 Partial-Bar Seeding (Plan 0) — Design

**Date:** 2026-06-25
**Status:** Approved (brainstorming) — ready for writing-plans
**Scope:** Close out vision **M6** by seeding live alt-bar accumulators with the **mid-bar (partial) state** from the warmup tail, so the first live bar continues the historical series seamlessly. Generalizes the accumulator resume seam beyond `RenkoAccumulator`'s single `long` and persists partial state as CAS JSON (§H). Plan 0 of the IB re-plan phase (`2026-06-25-livehost-ib-replan-phase-design.md`) — **independent of IB**; the live-alt-bar M6 closeout.

## Context

**What M6 already has (Plan 4, DONE):** one alt-bar accumulator engine in `Domain/Aggregation`, fed by two drivers (batch `AggregationPipeline`, live `TickAggregationBarSource`) — proven equal by the `BatchEqualsLiveGoldenTests` golden. **Threshold-freeze is done:** the live accumulator derives its threshold from the feed-id at construction (`ThresholdResolver.ResolveParsed`) and never re-derives, so live bars share the historical series' thresholds.

**What remains (this plan):** the **partial-bar seam**. When the warmup tail ends mid-bar — the threshold not yet crossed at the last historical source record — the live accumulator today opens a **fresh** bar at the first live tick (`_barEmpty = true`), losing the in-progress accumulation. That breaks the M6 golden property (batch ≡ replay ≡ live) at the warmup→live boundary. The fix: capture the trailing partial state on the batch side, persist it as CAS JSON, and restore it into the live accumulator at session start.

**Owner directive:** NOT in production — break freely for the cleanest end-state. The narrow Renko `long` resume seam is **replaced**, not widened; nothing left dead.

## The resume seam today (and why it's insufficient)

`IBarAccumulator` carries a Renko-specific seam:

```csharp
void SeedResumeState(long lastBrickClose) { }              // default no-op
bool TryGetResumeState(out long lastBrickClose) { … false } // default false
```

It is insufficient for M6 two ways:
1. **It captures only `_lastBrickClose`** — the inter-bar "wall." A mid-bar restart needs the *whole* in-progress bar (OHLC-so-far, threshold accumulator, running volume), which differs per family.
2. **Renko's `SeedResumeState` deliberately zeroes `_pendingVolume`/`_lastEmittedTs`** — correct for a *batch partition boundary* (pending volume is discarded at partition edges) but **wrong for a mid-bar live restart**, where pending volume must carry forward.

So this is a genuine generalization, not a type widening.

## Mid-bar state surface (correctness state only)

Persist only the **in-progress bar**; never telemetry (`_barsEmitted`/`_overshootSum`/`_maxOvershoot` reset per session — persisting them would double-count at `Complete()`). The frozen threshold is **not** resume state (already derived from the feed-id).

| Family | Mid-bar state to persist |
|---|---|
| **EqV / EqT / EqD** (`AccumulatorBase`) | `_barEmpty, _tsOpen, _open, _high, _low, _close, _thresholdAcc` (`Int128`), `_baseVolumeAcc` |
| **EqIV / EqID / EqIT** (imbalance) | the base fields + `_signedAccLong, _buyAccLong, _sellAccLong` |
| **Range** | `_barEmpty, _tsOpen, _open, _runningHigh, _runningLow, _close, _baseVolumeAcc` |
| **Renko** | `_seeded, _lastBrickClose, _pendingVolume, _lastEmittedTs` (drain `_queue` before save) |

## Design decisions (locked)

### 1. Generalized seam — typed `AccumulatorState`, serialized at the boundary
Replace the Renko `long` seam with a polymorphic one:

```csharp
bool TrySaveState(out AccumulatorState state);   // false when no meaningful state (base family, _barEmpty)
void RestoreState(in AccumulatorState state);
```

- `AccumulatorState` is a **typed Domain value record**, one derived record per family (`VolumeBarState`, `ImbalanceBarState`, `RangeBarState`, `RenkoBarState`, …). Each accumulator captures/restores its own fields — the same per-accumulator ownership boundary `SidecarSchema` and the old resume seam already established.
- **Open/closed for new bar types (primary maintainability goal — the alt-bar type list is NOT final; CumSum and others are coming).** The persisted form is `{ "typeCode": "EqV", "state": { …record fields… } }`, and the boundary deserializer resolves the concrete `AccumulatorState` type by `typeCode` through a single **co-located `AccumulatorEntry.StateType(typeCode)`** sibling to the existing `AccumulatorEntry.Open` switch — **NOT** a parallel `JsonDerivedType`/polymorphism registry. There is exactly one central discriminator (`typeCode`, already required by `Open`), never two. **Adding a new bar type is a localized change:** the accumulator class + its `XxxBarState` record + two one-line additions in `AccumulatorEntry` (`Open` + `StateType`); the boundary serializer and the golden `[Theory]` (parameterized over type codes) pick it up with zero further edits.
- **Domain stays serialization-free.** It holds the data records; the **Application/Infrastructure CAS-JSON layer** (where §H persistence lives) does the `System.Text.Json` (de)serialization (each `state` object is a plain non-polymorphic record once `StateType` has named its type). Domain carries no serialization attributes — matching how `Asset`/`SourceRecord` are pure data serialized elsewhere. (The relay `IFramePayload` self-serialization precedent does **not** apply: that's a GC-free hot-path choice; resume state is cold.)
- **`Int128` (`_thresholdAcc`) is string-encoded** in `VolumeBarState` — JSON has no native 128-bit integer; round-trip via `Int128.Parse`/`ToString` (invariant).
- **Return-contract preserved:** `TrySaveState` returns `false` when there's nothing worth seeding (base/Range family with `_barEmpty`); Renko always returns `true` (its wall + pending always matter). This generalizes the existing `bool TryGetResumeState` semantics, so call sites that branch on the bool keep working.
- **Does not obstruct a future accumulator-slice refactor.** Turning each accumulator into a self-registering slice (so even `Open` becomes zero-edit) is a separate, larger change and out of scope for M6 seeding — but if it happens, `StateType` folds into the same per-type registration as `Open` (one slice owns both), so this seam is forward-compatible with it.

### 2. Base-class reuse
`AccumulatorBase` implements `TrySaveState`/`RestoreState` **once** for the EqV/EqT/EqD trio (they share *all* of the base's state; only `ThresholdContribution` differs). Imbalance, Range, and Renko implement their own. This is why imbalance is standalone (it doesn't derive `AccumulatorBase`) — it owns its `_signedAccLong`/`_buyAccLong`/`_sellAccLong`.

### 3. Driver integration — batch saves, live restores + re-persists
- **Batch (`AggregationPipeline`):** at end of historical processing, instead of discarding the trailing partial at `Complete()`, call `TrySaveState` and persist it as CAS JSON keyed by `(instrument, bar-spec)` (§H, on `IFileStorage`). This is the warmup-tail seed.
- **Live (`TickAggregationBarSource`):** at construction/session start, load the CAS-JSON seed (if present) and `RestoreState` into the accumulator *before* the first live tick. On an ongoing basis (and on graceful shutdown), persist its own current partial state so a mid-bar live restart re-seeds correctly (§H durability).
- The old per-partition Renko resume call sites in `AggregationPipeline` migrate to the generalized seam (batch partition-boundary semantics — zeroed pending — become an explicit choice at that call site, distinct from the mid-bar save).

### 4. Persistence (CAS JSON, §H)
Partial-bar state is stored next to the alt-bar feed as CAS JSON via `IFileStorage` (the same CAS-on-`IFileStorage` mechanism used by `collection.json`/cursors), keyed by `(instrument, bar-spec)`. Cold path — written at warmup completion, at session shutdown, and periodically during a live session; read once at session start.

## Verification

The M6 golden property (**batch ≡ replay ≡ live**) must hold **across a mid-bar restart**, by construction (shared engine + restored partial state):

- Extend `BatchEqualsLiveGoldenTests`: split a source stream at a point **inside** an in-progress bar; run the batch driver to that split (capture + persist partial state), then run the live driver from the split with the restored state; assert the live-completed bar is **element-wise `Int64Bar`-equal** to the single-pass batch bar — for **all 8 alt-bar families** (incl. the imbalance trio's sidecar rows).
- Round-trip each `AccumulatorState` record through the boundary serializer (incl. `Int128` string-encoding for `VolumeBarState`, and Renko's `_pendingVolume`/`_lastEmittedTs` carry-forward).
- Renko regression: the migrated seam preserves the existing batch partition-boundary behavior (`RenkoAccumulatorTests`) while the new live mid-bar path carries pending volume forward.
- No telemetry leakage: a restored accumulator's `Complete()` stats reflect only the post-restore session (overshoot/barsEmitted not double-counted).

## Open points (deferred)

1. **Live re-persist cadence** — periodic vs only-on-shutdown for the ongoing live partial-state save. Lean on-rotation/on-shutdown (cold path); a periodic timer is a durability/perf tradeoff decided in writing-plans.
2. *(resolved)* **Polymorphism wiring** — settled on `typeCode`-discriminated `{typeCode, state}` with a co-located `AccumulatorEntry.StateType(typeCode)` resolver (no `JsonDerivedType` registry), for open/closed addition of new bar types. See decision #1.
