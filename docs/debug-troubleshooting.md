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

## Strange behavior of orders
Not sure if it's on strategy side or the debug displaying:
Orders moment of placing is working as expected: At the last confifmed peak we place a buy order.

### What's wrong

1. [DEBUG FE] The actual order price is unclear - there is a green circle below the bar, no price level drawn. 
#### Expected:
A horizontal line from the bar of placement until the bar of filling/cancellation. ORD label is redundant. Instead of a circle, there could be an arrow in the direction of the order.

2. [STRATEGY | DEBUGGER | BACKTEST ENGINE] I expect the order to be at the last confirmed ZZ peak. On the next bar after one marked with Circle + ORD there is a breakout of the last confirmed ZZ peak, but no fill is displayed.
#### Expected:
When orders placed at the last confirmed ZZ peak have the peak level broken, a FILL happens.

3. FILL event happen very rearly comparing to the order events displayed, they have a FILL label, which is also not necessary as ORD label; it's better to have another mark instead. But getting rid of ORD and FILL is not a priority, we can decide later on their replacement.