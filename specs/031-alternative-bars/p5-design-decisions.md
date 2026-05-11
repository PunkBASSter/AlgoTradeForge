# P5 — Range / Renko design decisions (Phase 5)

**Status:** Locked. Gates P5-2 through P5-15. Replaces the originally-scoped P5-6 ADR with broader phase coverage. Lifts the v1 non-goal in TRD §15 ("Range/Renko").

## Context

Phase 4 (Subscription redesign) just landed; the alt-bar pipeline now supports four type codes (`EqV`, `EqT`, `EqD`, `EqIV`) end-to-end. Phase 5 adds two more: `Range` (price-spread bars) and `Renko` (constant-brick bars). Both are **path-dependent** — emission is decided by the running price trajectory, not by a single source-record contribution.

The original P5 task list (P5-1..P5-6) collapsed several decisions into one ADR. This document widens the scope: it pins the accumulator semantics, the threshold-unit choice, the sidecar question, the multi-emit interface extension, and the source-kind restrictions before code is written.

## Decisions

### D1. Source-kind restriction: tick-only in v1

Range and Renko **MUST** be aggregated from `Tick` sources only. Time-bar sources (1m, 5m, 1h) and OHLC-only sources are rejected at the eligibility layer.

**Why:** A 1m time bar with `H=160, L=100` and `range_size=50` *should* emit three Range bars (100→150 and within-bar fills), but the source has no intra-minute information. Collapsing to one bar with realized range = 60 produces a ≥20% overshoot on every emit and silently distorts the `actual_overshoot_pct` fidelity metric. TRD §7 marks Range/Renko as "future" only on Tick rows for this reason. Re-opening time-bar Range/Renko is deferred to a later phase (would require either documenting the approximation or adding intra-bar volume profile data).

**Enforcement:**
- `EligibilityRules.ForSource` returns Range/Renko in `EligibleTypes` only for `SourceKind.Tick`. All non-Tick branches add explicit `IneligibleType` entries with reason `"Range/Renko require a tick source for fidelity in v1."` (P5-10).
- Defense-in-depth runtime guard in `AggregationPipeline.Run`: throws `InvalidOperationException` if `job.TypeCode is "Range" or "Renko" && job.Source.Kind != Tick` (catches private-API callers bypassing eligibility).

### D2. Range emission rule: running `H − L ≥ N` since bar open

A Range bar emits when the running high minus the running low exceeds the threshold, where high/low are the running OHLC extremes since the current bar's first record.

```
emit when (running_high − running_low) >= range_size
```

**Why:** Matches NinjaTrader / TradingView convention. Strategy authors recognise it. Alternative ("max(close) − min(close)") under-counts wicks and surprises users.

**State:** identical to existing accumulators — `_tsOpen, _open, _runningHigh, _runningLow, _close, _baseVolumeAcc, _threshold`.

**Multi-emit per record:** never. A tick has `H = L = price`, so a single record cannot trigger emission on its own — multiple ticks accumulate until spread crosses threshold. **Range emits exactly 0 or 1 bar per `TryAdvance` call**; the existing single-bar interface suffices.

**Range cannot extend `AccumulatorBase`:** that base derives emission from a `ThresholdContribution` long, not from an OHLC delta. Range implements `IBarAccumulator` directly (mirrors `EqIAccumulator`'s shape).

### D3. Renko variant: 1× neutral bricks (no 2× reversal)

Renko bars use **constant-brick** semantics: a brick emits whenever price moves `brick_size` from the previous brick's close, in either direction. No 2× reversal threshold.

**Why:** Simpler. Matches QuantConnect / Lean / pyalgotrade default. v1 produces more bars (good for backtest density). Traditional 2× Renko can land later behind a manifest flag if there's demand; v1 keeps the manifest shape unchanged.

### D4. Renko OHLC: clean rectangles (no wicks)

For each emitted brick:
- `open = previousBrickClose`
- `close = open ± brick_size` (sign from price direction)
- `high = max(open, close)`
- `low = min(open, close)`
- **Wick / actual price extremes are discarded.**

**Why:** Renko's primary use is visualization and direction detection. "Bricks are clean rectangles" is the invariant strategy authors expect. Surfacing actual-price-during-brick info would either (a) inflate `high`/`low` outside the brick range (breaks the invariant) or (b) require a sidecar (rejected — see D7).

If a future phase needs wick data, it can add a `.brick` sidecar with `wick_high`, `wick_low` columns. The `IFeedContext.TryGetPrimarySidecar` infrastructure (Phase 2b) already supports lazy-loaded sidecars by manifest field — extending later is non-breaking.

### D5. Renko volume: distribute proportionally across bricks

When one tick crosses N bricks, the tick's volume is distributed:
- Each of the first `N − 1` bricks receives `tick.volume / N`
- The last brick receives the remainder (`tick.volume − (N−1) × (tick.volume / N)`)
- Pending volume from earlier ticks (that didn't yet trigger a brick) is added to the first brick of the chain.

**Why:** Preserves `Σ brick.vol == Σ tick.qty` exactly (modulo integer rounding into the last brick). Strategies summing volume across bricks see the same total as summing the underlying ticks. Alternative ("all volume on the final brick") is simpler but breaks the conservation invariant and produces zero-volume bricks in the middle of multi-brick chains.

### D6. Multi-brick emission: internal queue + new DIM

Renko's path-dependence means a single tick can emit 0, 1, or many bricks. The existing `IBarAccumulator.TryAdvance(record, out emitted)` returns one bar per call. We extend the interface with one default-interface method:

```csharp
bool TryDrainQueued(out AggregatedBar emitted)
```

**Default impl returns `false`**, so EqV/EqT/EqD/EqIV inherit no-op behaviour automatically. Renko enqueues bricks 2..N internally; `TryAdvance` returns brick 1 via `out`, and the pipeline drains the rest in a `while (acc.TryDrainQueued(...))` loop after each emit.

**Why this shape:**
- Additive — no breakage to existing accumulators (P0-1 audit pre-cleared DIM dispatch on plugin assemblies)
- Pipeline diff is ~6 lines around the existing emit handler
- Mirrors the existing `TryGetLastSidecarRow` DIM pattern (lives on the same interface, returns `false` for non-EqIV)
- Alternative (`IEnumerable<AggregatedBar>` return type) breaks every accumulator's signature

### D7. No sidecar for either type

Range and Renko **do not** publish a `.flow` sidecar or any other sidecar in v1.

**Why:**
- **Range:** `realized_range = bar.High − bar.Low` is reconstructible from the primary OHLC at zero cost. Per-bar `realized_threshold` for fidelity belongs in the manifest's `fidelity` block (already populated from `AggregationStats`), not a per-bar sidecar.
- **Renko:** `direction = sign(close − open)` is reconstructible from primary OHLC. Wick data is intentionally discarded (D4). The only column that *isn't* reconstructible is "actual price extremes seen during the brick" — and we explicitly chose not to surface that.

**Blast-radius reduction:** the pipeline currently has 5 hardcoded `isEqI` checks (`AggregationPipeline.cs:77, 82, 119, 145, 248`) for sidecar staging and the `imbalance_reconstruction_method` manifest tag. Choosing no-sidecar means **none of those sites change**. Phase 5 stays additive.

If a future phase wants Range/Renko sidecars, generalize via a new `IBarAccumulator.SidecarSchema { get; }` property at that time.

### D8. New threshold unit: `"price"`

Both Range and Renko thresholds are price magnitudes (e.g., $50 per bar, 100 ticks per brick). A new `ThresholdResolver` unit `"price"` lands in P5-0:

```csharp
"price" => scale.AmountToTicks(absolute),
```

**Why no `* QuantityScale` factor:** unlike `base_asset` and `quote_asset` (which scale by `QuantityScale` because EqD/EqV contributions are in volume units), a price threshold scales the same way prices themselves scale — straight `AmountToTicks`. Including `* QuantityScale` would silently inflate thresholds by orders of magnitude on assets with non-unit `QuantityScale` (e.g., BTCUSDT_perp where `QuantityScale = 1000`).

**Convenience input:** SI suffixes (`"50"`, `"1k"`, `"5m"`) work via the existing parser. Sub-unit thresholds (`"50m"` for $0.05) round-trip correctly.

### D9. `imbalance_reconstruction_method` manifest field stays null for Range/Renko

The manifest's `fidelity.imbalance_reconstruction_method` (P1a-6 invariant) is an EqIV-specific tag. For Range/Renko it stays `null`. The validator at write/read enforces this implicitly (the field is required, but `null` is the only valid value for non-EqIV types — no change needed).

We do **not** add a parallel `range_reconstruction_method` or `renko_reconstruction_method` field in v1 — they'd always be `"tick"` (D1) and would be removed when we eventually add time-bar variants. Wait until there's >1 method to introduce the field.

## Risks / future work

1. **Q-3 (threshold floor) intersects `price` unit.** Current `MinimumThresholdAbsolute()` returns `1m` (1 unit) — which means $1 floor for Range. Too coarse for BTC (tick = $0.10), too fine for SHIB. Q-3 stays open; Range can ship with the existing floor and Q-3 lifts it later.
2. **Wick data is unrecoverable.** If a future phase wants traditional Renko-with-shadows visualization, it will need a sidecar. The infrastructure supports it (Phase 2b's `IFeedContext.TryGetPrimarySidecar` is sidecar-name-agnostic), but adding a `.brick` sidecar in a later phase will mean re-aggregating any Range/Renko feeds built without it.
3. **Volume conservation under integer division.** `tick.volume / N` truncates; the last brick takes the remainder. Strategies that sum volume across bricks should match tick-volume sums exactly. Documented in `RenkoAccumulator` and pinned by a test.
4. **2× reversal Renko** is the obvious follow-up if backtesting reveals 1× neutral bricks emit too many false-direction signals. Manifest shape would need a `renko_variant: "neutral_1x" | "reversal_2x"` field.

## Cross-references

- **TRD §6.3** — accumulator table; P5-13 adds Range/Renko rows
- **TRD §7** — eligibility matrix; P5-13 narrows row 1 ("future Range/Renko" → "Range/Renko") and row 4 (no Range/Renko on OHLC-only)
- **TRD §15** — non-goals; P5-13 strikes the Range/Renko line
- **P0-5 ADR** — threshold wire schema (`input_mode`, `convenience_input`); D8 adds `"price"` to the unit set
- **P1a-6 / P1b-3** — `fidelity.imbalance_reconstruction_method` invariant (always present, null for non-EqIV); D9 confirms unchanged
- **P2b-9 / Phase 2b** — `IFeedContext.TryGetPrimarySidecar`; D7 leaves untouched
