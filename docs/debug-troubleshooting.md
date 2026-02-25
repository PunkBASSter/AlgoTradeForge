# Debug troubleshooting

## 0. Same pre-requisites for all next cases:
Debug was launched with settings:
```
{
  "assetName": "BTCUSDT",
  "exchange": "Binance",
  "strategyName": "ZigZagBreakout",
  "initialCash": 10000,
  "startTime": "2025-01-01T00:00:00Z",
  "endTime": "2025-12-31T23:59:59Z",
  "commissionPerTrade": 0.001,
  "slippageTicks": 2,
  "timeFrame": "01:00:00",
  "strategyParameters": {
    "DzzDepth": 5,
    "MinimumThreshold": 10000,
    "RiskPercentPerTrade": 1,
    "MinPositionSize": 0.01,
    "MaxPositionSize": 1000
  }
}
```
Not sure if it's possible to set explicit data subscription and specify the exportable flag (as allowed in domain).
Steps
When the Debug start submitted, in the console I see:
```
[DebugWebSocket] Connecting {sessionId: 'b536fee4-f5f6-4cfb-b5ae-23436c47101e', wsUrl: 'ws://localhost:5000/api/debug-sessions/b536fee4-f5f6-4cfb-b5ae-23436c47101e/ws'}sessionId: "b536fee4-f5f6-4cfb-b5ae-23436c47101e"wsUrl: "ws://localhost:5000/api/debug-sessions/b536fee4-f5f6-4cfb-b5ae-23436c47101e/ws"[[Prototype]]: Object
logger.ts:32 [DebugWebSocket] Connected
```
Looks good at this stage.
All next parragrahp assume this step executed beforehand.

## 1. Restart debug - sometimes no events
1. Press any control button, e.g. `Next` or `To Next Bar`

### Actual Result:
In console:
```
[DebugWebSocket] Run ended {totalBarsProcessed: 35040, finalEquity: 1071193, totalFills: 236, duration: '00:01:32.1239239'}
``` 
and in the Session Metrics panel:
```
Session Active
No
Sequence
35040
Timestamp
2025-12-31 23:45:00.000 UTC
Portfolio Equity
1,071,193.00
Fills This Bar
0
Subscription Index
0
```
On the chart NOTHING is drawn.

### Expected result
1. Debug is controlled by the control buttons and does not look finished
2. The data is displayed on the chart (prices, orders, trades, fills, indicators)

## 2. What is Export Off/On? Deoes it start the events stream?
1. I press `Export Off/On` ending up with `ON`, see ```[DebugWebSocket] Export ack {mutations: true}``` in the console. This is a bit confusing from UX perspective.
2. I wait a minute and press `Next`, seeing in the console:

### Actual Result
In Console
```
[DebugWebSocket] Run ended {totalBarsProcessed: 35040, finalEquity: 1071193, totalFills: 236, duration: '00:01:32.1239239'}
``` 
and in the Session Metrics panel:
```
Session Active
No
Sequence
35040
Timestamp
2025-12-31 23:45:00.000 UTC
Portfolio Equity
1,071,193.00
Fills This Bar
0
Subscription Index
0
```
On the chart NOTHING is drawn.

### Expected result
1. Debug is controlled by the control buttons and does not look finished
2. The data is displayed on the chart (prices, orders, trades, fills, indicators)

## Conclusion
Initially I managed to get some Session Active: Yes in Session Metrics panel, and for `To Next Bar` there were bar timestamps changing (but still nothing was drawn on the chart), but after several restarting attempts I never saw Session Active: Yes any more.
Receiving only:
```
[DebugWebSocket] Connecting {sessionId: 'e6c2920f-3c39-4ba3-a33d-4f789afbca56', wsUrl: 'ws://localhost:5000/api/debug-sessions/e6c2920f-3c39-4ba3-a33d-4f789afbca56/ws'}
logger.ts:32 [DebugWebSocket] Connected
logger.ts:30 [DebugWebSocket] Run ended {totalBarsProcessed: 35040, finalEquity: 1071193, totalFills: 236, duration: '00:02:51.0051107'}
logger.ts:32 [DebugWebSocket] Disconnected
logger.ts:30 [DebugWebSocket] Connecting {sessionId: '0a9738e0-a920-4669-8236-c4af8e5c1541', wsUrl: 'ws://localhost:5000/api/debug-sessions/0a9738e0-a920-4669-8236-c4af8e5c1541/ws'}
logger.ts:32 [DebugWebSocket] Connected
logger.ts:30 [DebugWebSocket] Run ended {totalBarsProcessed: 35040, finalEquity: 1071193, totalFills: 236, duration: '00:00:01.2663981'}
logger.ts:32 [DebugWebSocket] Disconnected
logger.ts:30 [DebugWebSocket] Connecting {sessionId: '782fcc57-5426-485f-b11e-d33ae7704bec', wsUrl: 'ws://localhost:5000/api/debug-sessions/782fcc57-5426-485f-b11e-d33ae7704bec/ws'}
logger.ts:32 [DebugWebSocket] Connected
logger.ts:30 [DebugWebSocket] Run ended {totalBarsProcessed: 35040, finalEquity: 1071193, totalFills: 236, duration: '00:00:00.6884065'}
```
from several last attempts.