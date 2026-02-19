---
description: Run a parameter optimization via the AlgoTradeForge WebApi
---

## User Input

```text
$ARGUMENTS
```

## Instructions

You are running a brute-force parameter optimization against the AlgoTradeForge WebApi. Parse the user input to extract parameters, fill in defaults for anything not specified, then execute the optimization.

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

**Optimization axes** control which parameters to sweep. Three override formats are supported:

- **Range**: `{ "min": 1, "max": 10, "step": 1 }` — sweep from min to max
- **Fixed**: `{ "fixed": 5 }` — lock to a single value
- **Discrete set**: `{ "values": [1, 3, 5, 10] }` — try specific values

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

### 3. Execute Optimization

Send the POST request:

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

**Important:** Axis keys must be PascalCase (e.g. `DzzDepth`, not `dzzDepth`).

**Note:** This call can take a long time depending on the number of combinations. Use a generous timeout (up to 10 minutes). Inform the user of the estimated combination count before sending.

### 4. Display Results

Format the JSON response as a readable summary. Show the top 10 trials (or fewer if less returned):

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
    Net Profit: ${netProfit}  |  Return: {totalReturnPct}%
    Sharpe: {sharpe}  |  Sortino: {sortino}  |  Max DD: {maxDD}%
    Trades: {totalTrades}  |  Win Rate: {winRate}%  |  PF: {profitFactor}

#2  ...
```

After the top 10, add a brief summary line:

```
Best {sortBy}: #{rank} with {bestMetricValue}
```

### 5. Cleanup

Do NOT stop the API after the optimization. Leave it running for subsequent requests.
