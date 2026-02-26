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
  "timeFrame": "00:15:00",
  "strategyParameters": {
    "DzzDepth": 5,
    "MinimumThreshold": 10000,
    "RiskPercentPerTrade": 1
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

## Strange behavior of orders
Not sure if it's on strategy side or the debug displaying:
Orders moment of placing is working as expected: At the last confifmed peak we place a buy order.

### What's wrong
Screenshot:
@docs\rejection_examples.png

Session Metrics:
```
Session Active
Yes
Sequence
46
Timestamp
2025-01-01 11:15:00.000 UTC
Portfolio Equity
1,000,000.00
Fills This Bar
0
Subscription Index
0
```
and
Session Active
Yes
Sequence
50
Timestamp
2025-01-01 12:15:00.000 UTC
Portfolio Equity
1,000,000.00
Fills This Bar
0
Subscription Index
0

Why the orders got rejected?
I expected to get a fill at sequence #46 and #50.

