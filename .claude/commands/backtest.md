---
description: Run a backtest via the AlgoTradeForge WebApi
---

## User Input

```text
$ARGUMENTS
```

## Instructions

You are running a backtest against the AlgoTradeForge WebApi. Parse the user input to extract parameters, fill in defaults for anything not specified, then execute the backtest.

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

### 3. Execute Backtest

Send the POST request:

```bash
curl -s -X POST https://localhost:55908/api/backtests/ \
  -H "Content-Type: application/json" -k \
  -d '<JSON payload>'
```

**Important:** Strategy parameter keys must be PascalCase (e.g. `DzzDepth`, not `dzzDepth`).

### 4. Display Results

Format the JSON response as a readable summary table:

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

### 5. Cleanup

Do NOT stop the API after the backtest. Leave it running for subsequent requests.
