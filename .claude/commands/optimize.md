---
description: Run a parameter optimization via the AlgoTradeForge WebApi
---

## User Input

```text
$ARGUMENTS
```

## Instructions

You are running a brute-force parameter optimization against the AlgoTradeForge WebApi. The API uses an async submit-and-poll pattern: POST submits the run and returns immediately with an ID, then you poll for status until the result is ready.

### 1. Parse Parameters

Extract these from the user input (defaults shown in parentheses):

| Parameter | Default |
|---|---|
| `strategyName` | `ZigZagBreakout` |
| `initialCash` | `10000` |
| `startTime` | `2024-01-01T00:00:00Z` |
| `endTime` | `2026-01-31T23:59:59Z` |
| `commissionPerTrade` | `2` |
| `slippageTicks` | `1` |
| `maxDegreeOfParallelism` | `-1` (use all cores) |
| `maxCombinations` | `100000` |
| `sortBy` | `SharpeRatio` |

**Data subscriptions** (defaults to a single subscription if not specified):

| Parameter | Default |
|---|---|
| `asset` | `BTCUSDT` |
| `exchange` | `Binance` |
| `timeFrame` | `01:00:00` (H1) |

Multiple subscriptions can be specified by the user (e.g. "BTCUSDT H1 and ETHUSDT H1").

**Optimization axes** control which parameters to sweep. Four override formats are supported:

- **Range**: `{ "min": 1, "max": 10, "step": 1 }` — sweep from min to max
- **Fixed**: `{ "fixed": 5 }` — lock to a single value
- **Discrete set**: `{ "values": [1, 3, 5, 10] }` — try specific values
- **Module choice**: `{ "variants": { "VariantA": { ... }, "VariantB": { ... } } }` — sweep pluggable module variants

**Module-dependent parameters** — Strategy params can contain `[OptimizableModule]` slots (e.g., `ExitModule`, `TrendFilter`). Each module slot has multiple registered variants, and each variant has its own sub-parameters. Key behavior:

- Parameters are **variant-scoped**: when variant A is selected for a trial, only A's sub-parameters are present. Variant B's parameters do not exist in that trial, and vice versa.
- Variants are **additive** across the module slot: if A has 6 sub-combos and B has 3, the module slot contributes 6 + 3 = 9 trials (not 18).
- Sub-parameters use the same override formats (range, fixed, discrete set). Nested module slots are supported recursively.
- Only variants listed in the request are included. Omitting a variant excludes it entirely.

Module choice JSON format:

```json
"ExitModule": {
  "variants": {
    "AtrExit": {
      "Multiplier": { "min": 2.0, "max": 3.0, "step": 0.5 },
      "Period": { "min": 14, "max": 28, "step": 7 }
    },
    "FibTp": {
      "Level": { "fixed": 0.5 }
    }
  }
}
```

In results, module parameters appear as nested objects: `ExitModule=AtrExit(Multiplier=2.5, Period=14)`.

If the user doesn't specify axes, use the strategy's default ranges below.

**ZigZagBreakout default axes** (derived from `[Optimizable]` attributes):

| Parameter | Min | Max | Step | Combinations |
|---|---|---|---|---|
| `DzzDepth` | 1 | 20 | 0.5 | 39 |
| `MinimumThreshold` | 5000 | 50000 | 5000 | 10 |
| `RiskPercentPerTrade` | 0.5 | 3 | 0.5 | 6 |

Full sweep = 2,340 combinations. If the user asks for a "quick" optimization, use narrower ranges:

| Parameter | Min | Max | Step | Combinations |
|---|---|---|---|---|
| `DzzDepth` | 3 | 10 | 1 | 8 |
| `MinimumThreshold` | 10000 | 40000 | 10000 | 4 |
| `RiskPercentPerTrade` | 0.5 | 2 | 0.5 | 4 |

Quick sweep = 128 combinations.

**Valid `sortBy` values:** `SharpeRatio`, `NetProfit`, `SortinoRatio`, `ProfitFactor`, `WinRatePct`, `MaxDrawdownPct`

**Known assets and exchanges:**
- BTCUSDT (Binance), ETHUSDT (Binance)
- AAPL (NASDAQ), MSFT (NASDAQ)
- ES (CME), MES (CME)

### 2. Ensure API is Running

Check if the API is already listening:

```bash
curl -sk -o /dev/null -w "%{http_code}" https://localhost:55908/swagger/v1/swagger.json
```

If not reachable (non-200), start it:

```bash
dotnet run --project src/AlgoTradeForge.WebApi 2>&1
```

Wait a few seconds and verify it's listening before proceeding.

### 3. Submit Optimization

Send the POST request. The API returns **202 Accepted** with a submission response (not the final result):

```bash
curl -s -X POST https://localhost:55908/api/optimizations/ \
  -H "Content-Type: application/json" -k \
  -d '<JSON payload>'
```

**JSON payload structure:**

```json
{
  "strategyName": "ZigZagBreakout",
  "dataSubscriptions": [
    { "asset": "BTCUSDT", "exchange": "Binance", "timeFrame": "01:00:00" }
  ],
  "optimizationAxes": {
    "DzzDepth": { "min": 1, "max": 20, "step": 0.5 },
    "MinimumThreshold": { "min": 5000, "max": 50000, "step": 5000 },
    "RiskPercentPerTrade": { "min": 0.5, "max": 3, "step": 0.5 }
  },
  "initialCash": 10000,
  "startTime": "2024-01-01T00:00:00Z",
  "endTime": "2026-01-31T23:59:59Z",
  "commissionPerTrade": 3,
  "slippageTicks": 1,
  "maxDegreeOfParallelism": -1,
  "maxCombinations": 100000,
  "sortBy": "SharpeRatio"
}
```

**Payload with module slots** (when the strategy has `[OptimizableModule]` properties):

```json
{
  "strategyName": "ZigZagBreakout",
  "dataSubscriptions": [
    { "asset": "BTCUSDT", "exchange": "Binance", "timeFrame": "01:00:00" }
  ],
  "optimizationAxes": {
    "DzzDepth": { "min": 3, "max": 10, "step": 1 },
    "RiskPercentPerTrade": { "fixed": 1.0 },
    "ExitModule": {
      "variants": {
        "AtrExit": {
          "Multiplier": { "min": 1.5, "max": 3.0, "step": 0.5 },
          "Period": { "min": 14, "max": 28, "step": 7 }
        },
        "FibTp": {
          "Level": { "min": 0.382, "max": 0.786, "step": 0.202 }
        }
      }
    }
  },
  "initialCash": 10000,
  "startTime": "2024-01-01T00:00:00Z",
  "endTime": "2026-01-31T23:59:59Z",
  "commissionPerTrade": 3,
  "slippageTicks": 1,
  "maxDegreeOfParallelism": -1,
  "maxCombinations": 100000,
  "sortBy": "SharpeRatio"
}
```

**Important:** Axis keys must be PascalCase (e.g. `DzzDepth`, not `dzzDepth`).

**Submission response:** `{ "id": "<guid>", "totalCombinations": <long> }`

Capture the `id` for polling. Tell the user the optimization was submitted and the total combination count.

### 4. Poll for Completion

Poll the status endpoint until the `result` field is non-null:

```bash
curl -s -k https://localhost:55908/api/optimizations/<id>/status
```

**Status response:**
```json
{
  "id": "<guid>",
  "completedCombinations": 500,
  "totalCombinations": 2340,
  "result": null
}
```

- While `result` is `null`: the run is still in progress. Report progress (`completedCombinations/totalCombinations`) and wait before polling again.
- When `result` is non-null: the run is complete. The `result` contains the full optimization data with all trials.

**Polling strategy:**
- Poll every 5 seconds initially
- Show a progress update to the user each poll (e.g. "Processing: 500/2340 combinations (21%)")
- Use a maximum timeout of 30 minutes for large optimizations

### 5. Display Results

Once `result` is available, format it as a readable summary. Show the top 10 trials (or fewer if less returned):

```
Optimization Results: {strategyName}
================================================
Data:                {subscriptions summary}
Period:              {startTime} to {endTime}
Sorted by:           {sortBy}
Total Combinations:  {totalCombinations}
Total Duration:      {totalDuration}

Top 10 Parameter Sets (by {sortBy}):
─────────────────────────────────────

#1  DzzDepth={val}  MinimumThreshold={val}  RiskPercentPerTrade={val}
    ExitModule=AtrExit(Multiplier=2.5, Period=14)
    Net Profit: ${netProfit}  |  Return: {totalReturnPct}%
    Sharpe: {sharpe}  |  Sortino: {sortino}  |  Max DD: {maxDD}%
    Trades: {totalTrades}  |  Win Rate: {winRate}%  |  PF: {profitFactor}

#2  ...
```

The trials are in `result.trials[]`. Each trial has `parameters` (dict), `metrics` (camelCase keys), `errorMessage` (if the trial failed). Skip failed trials in the ranking.

After the top 10, add a brief summary line:

```
Best {sortBy}: #{rank} with {bestMetricValue}
```

### 6. Cleanup

Do NOT stop the API after the optimization. Leave it running for subsequent requests.
