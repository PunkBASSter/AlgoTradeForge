# P0-5 — Threshold input semantics: wire schema (Phase 0)

**Status:** Locked. Gates P1a-5 (manifest schema). Resolves TRD §12 item 1.

## Decision

Three rules govern threshold values across the wire / on disk:

1. **`feeds.json` always stores absolute canonical units.** No SI suffixes, no convenience expressions, no scaled longs. The integer value (with optional fractional component captured via the SI mantissa form for sub-unit thresholds) is the source of truth.
2. **The aggregation request payload carries an explicit `input_mode`** ∈ `{ "absolute", "convenience" }`. The server resolves convenience to absolute at job creation; the original input is preserved in `threshold.convenience_input` for traceability.
3. **`threshold.convenience_input`** is required when `input_mode = "convenience"` and `null` otherwise. Round-trips through `feeds.json` so audit / replay stays exact.

## Wire schemas

### Aggregation request (`POST /api/v1/exchanges/{ex}/assets/{asset}/aggregate`)

```json
{
  "source_feed_id": "1m",
  "type_code": "EqV",
  "threshold": 1000,                 // numeric — see "Threshold value form" below
  "threshold_unit": "base_asset",    // "base_asset" | "quote_asset" | "trades"
  "input_mode": "absolute",          // "absolute" | "convenience"
  "convenience_input": null,         // string | null; required iff input_mode == "convenience"
  "overwrite_existing": false
}
```

### `feeds.json` aggregated entry (relevant subset)

```json
{
  "feeds": {
    "EqV_1m_1000": {
      "kind": "aggregated",
      "type": { "code": "EqV", "name": "EqualVolume" },
      "source": { "feed": "1m", "...": "..." },
      "threshold": {
        "value": 1000,                     // absolute, canonical unit
        "unit": "base_asset",
        "input_mode": "absolute",          // echoed verbatim from request
        "convenience_input": null          // echoed verbatim from request
      },
      "...": "..."
    }
  }
}
```

## Threshold value form

The `threshold` field accepts a positive integer **mantissa** with an optional SI suffix appended directly (no separator):

| Suffix | Meaning | Example |
|---|---|---|
| (none) | × 1 | `1000` → 1000 base units |
| `k` | × 10³ | `1k` → 1000 |
| `M` | × 10⁶ | `1M` → 1 000 000 |
| `G` | × 10⁹ | `1G` → 1 000 000 000 |
| `m` | × 10⁻³ | `500m` → 0.5 base units |
| `u` | × 10⁻⁶ | `1u` → 1e-6 base units (minimum effective threshold) |

**Rule:** below `1u` of the canonical unit, the eligibility endpoint (TRD §5.3) rejects the request before enqueue.

The mantissa-with-suffix form keeps a pure-integer FeedId (`EqV_1m_500m`) — no `.` ever lands in a filename or path component, preserving filesystem/lex sort semantics.

JSON encoding accepts EITHER:
- A bare number (interpreted as suffix-less, e.g. `1000`)
- A string with mantissa+suffix (`"500m"`, `"1k"`)

The server normalizes both into a canonical `(mantissa, suffix)` pair at parse time and stores the absolute decimal value in `feeds.json` `threshold.value`.

## Convenience-mode resolution

When `input_mode == "convenience"`, `convenience_input` is a free-form string the FE sends verbatim from the user's input field (e.g., `"1k 1m"` for "1k of 1-minute candles" — a future UX). v1 may reject all convenience inputs with a 422; the field is wired now to lock the contract for Phase 6 expansion.

The server's resolution rule: *parse `convenience_input` → produce the absolute integer threshold → store in `threshold.value`; echo the original string in `threshold.convenience_input`.* The conversion is one-way at write time; the consumer (UI / replay) reads the absolute and the convenience side-by-side.

## Why this shape

- **Absolute storage** is forward-stable: future code that reads the manifest doesn't need to evaluate convenience expressions.
- **Explicit `input_mode`** prevents wire-level ambiguity. Without it, a server can't tell whether `1000` is "1000 base units" or "1k as a typo for 1k 1m".
- **Echoing convenience verbatim** is a debugging aid: an analyst replaying an aggregation knows what the user typed, not just what the server computed.
- **Mantissa+suffix integer form** gives sub-unit thresholds (`500m`) without ever introducing `.` into FeedIds, paths, or sort keys.

## Validation rules (write-side, Phase 1a P1a-5)

The manifest writer rejects:
- `threshold.value < 1u` of canonical unit.
- `input_mode == "convenience"` AND `convenience_input == null` (or empty).
- `input_mode == "absolute"` AND `convenience_input != null`.
- `threshold_unit ∉ { "base_asset", "quote_asset", "trades" }`.
- Inconsistency between `type_code` and `threshold_unit` (e.g., `EqT` MUST pair with `"trades"`; `EqV` with `"base_asset"`; `EqD`/`EqIV` with `"quote_asset"`) — pinned per TRD §3.4 table.

These rules are wired in P1a-5; the eligibility endpoint (Phase 1b P1b-26) surfaces them as 422 + `ProblemDetails`.
