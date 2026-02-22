---
description: Run a backtest via the AlgoTradeForge WebApi
---

## User Input

```text
$ARGUMENTS
```

## Instructions

You are running a backtest against the AlgoTradeForge WebApi. The API uses an async submit-and-poll pattern: POST submits the run and returns immediately with an ID, then you poll for status until the result is ready.

### 1. Parse Parameters

Extract these from the user input (defaults shown in parentheses):

| Parameter | Default |
|---|---|
| `assetName` | `BTCUSDT` |
| `exchange` | `Binance` |
| `strategyName` | `ZigZagBreakout` |
| `initialCash` | `10000` |
| `startTime` | `2024-01-01T00:00:00Z` |
| `endTime` | `2026-01-31T23:59:59Z` |
| `commissionPerTrade` | `3` |
| `slippageTicks` | `1` |
| `timeFrame` | `01:00:00` (H1) |

**Strategy parameters** depend on the strategy. Known strategies and their params (use PascalCase keys):

**ZigZagBreakout:**
- `DzzDepth` (decimal, default: 5)
- `MinimumThreshold` (long, default: 10000)
- `RiskPercentPerTrade` (decimal, default: 1)
- `MinPositionSize` (decimal, default: 0.01)
- `MaxPositionSize` (decimal, default: 1000)

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

### 3. Submit Backtest

Send the POST request. The API returns **202 Accepted** with a submission response (not the final result):

```bash
curl -s -X POST https://localhost:55908/api/backtests/ \
  -H "Content-Type: application/json" -k \
  -d '<JSON payload>'
```

**Important:** Strategy parameter keys must be PascalCase (e.g. `DzzDepth`, not `dzzDepth`).

**Submission response:** `{ "id": "<guid>", "totalBars": <int> }`

Capture the `id` for polling. Tell the user the backtest was submitted and how many bars will be processed.

### 4. Poll for Completion

Poll the status endpoint until the `result` field is non-null:

```bash
curl -s -k https://localhost:55908/api/backtests/<id>/status
```

**Status response:**
```json
{
  "id": "<guid>",
  "processedBars": 500,
  "totalBars": 1000,
  "result": null
}
```

- While `result` is `null`: the run is still in progress. Report progress (`processedBars/totalBars`) and wait before polling again.
- When `result` is non-null: the run is complete. The `result` contains the full backtest data.

**Polling strategy:**
- Poll every 3 seconds initially
- Show a progress update to the user each poll (e.g. "Processing: 500/1000 bars (50%)")
- Use a maximum timeout of 10 minutes

**Error/cancellation detection** (check `result` fields when present):
- `result.runMode === "Cancelled"` → run was cancelled
- `result.errorMessage` is non-null → run failed with an error

### 5. Display Results

Once `result` is available, format it as a readable summary table:

```
Backtest Results: {strategyName} on {assetName}
================================================
Period:              {startTime} to {endTime}
Duration:            {duration}

Capital:
  Initial:           ${initialCapital}
  Final Equity:      ${finalEquity}
  Net Profit:        ${netProfit}
  Total Return:      {totalReturnPct}%

Risk Metrics:
  Max Drawdown:      {maxDrawdownPct}%
  Sharpe Ratio:      {sharpeRatio}
  Sortino Ratio:     {sortinoRatio}

Trade Statistics:
  Total Trades:      {totalTrades}
  Win Rate:          {winRatePct}%
  Profit Factor:     {profitFactor}
```

The metrics are in `result.metrics` as camelCase keys (e.g. `netProfit`, `sharpeRatio`, `maxDrawdownPct`).

### 6. Cleanup

Do NOT stop the API after the backtest. Leave it running for subsequent requests.
