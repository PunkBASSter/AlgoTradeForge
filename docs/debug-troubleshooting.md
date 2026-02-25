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

## Debug fail after 2 consequent `To Next Bar`
1. Start debugging
2. Click `To Next Bar` several times

### Actual Result
In console
```
error-boundary-callbacks.ts:90 
 Error: Assertion failed: data must be asc ordered by time, index=2, time=1772052643, prev time=1772052643
    at CandlestickChart.useEffect (candlestick-chart.tsx:228:16)


The above error occurred in the <CandlestickChart> component. It was handled by the <ErrorBoundaryHandler> error boundary.
onCaughtError	@	error-boundary-callbacks.ts:90
logCaughtError	@	react-dom-client.development.js:9772
runWithFiberInDEV	@	react-dom-client.development.js:986
update.callback	@	react-dom-client.development.js:9805
callCallback	@	react-dom-client.development.js:7735
commitCallbacks	@	react-dom-client.development.js:7755
runWithFiberInDEV	@	react-dom-client.development.js:986
commitClassCallbacks	@	react-dom-client.development.js:13820
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15060
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14988
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14988
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14988
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14988
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15099
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15099
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15099
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14988
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14988
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14988
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14988
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:14983
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15204
recursivelyTraverseLayoutEffects	@	react-dom-client.development.js:16370
commitLayoutEffectOnFiber	@	react-dom-client.development.js:15065
flushLayoutEffects	@	react-dom-client.development.js:19570
commitRoot	@	react-dom-client.development.js:19335
commitRootWhenReady	@	react-dom-client.development.js:18178
performWorkOnRoot	@	react-dom-client.development.js:18054
performSyncWorkOnRoot	@	react-dom-client.development.js:20399
flushSyncWorkAcrossRoots_impl	@	react-dom-client.development.js:20241
processRootScheduleInMicrotask	@	react-dom-client.development.js:20280
(anonymous)	@	react-dom-client.development.js:20418
logger.ts:32 [DebugWebSocket] Disconnected
```
On the page:
```
Assertion failed: data must be asc ordered by time, index=2, time=1772052643, prev time=1772052643
```

Assumption: maybe mutation events or other event types were received and attempted to be rendered on the chart causing an error