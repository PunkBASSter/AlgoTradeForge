# Live connector for Binance

## Known Resilience Gaps

### 1. No application-level heartbeat

Connection loss detection relies on OS TCP timeout, which can take minutes for half-open connections (e.g., when a NAT gateway silently drops the session). A periodic ping frame or proactive `listenKey` renewal (PUT every ~30 min) would detect stale WebSocket connections much faster and trigger reconnection sooner.

### 2. No recovery after max reconnect attempts

After exhausting the 10 configured reconnect attempts with exponential backoff, the connector stops completely. There is no automatic recovery path, no alerting mechanism, and no escalation beyond what Serilog logs. In production this means the strategy silently goes offline. Options to close this gap include: a supervisor loop that resets the attempt counter after a cooldown period, an alert via webhook/email, or an operator-facing health check endpoint that surfaces the failure.

### 3. Cancel failures during shutdown are not retried

If a cancel API call fails in `ProcessCancelsAsync` (e.g., due to a transient network error or rate limit), the order remains open on Binance. The current implementation logs the failure but does not retry. A bulk "cancel all open orders" REST call (`DELETE /api/v3/openOrders`) as a final safety net during graceful shutdown could prevent orphaned orders from lingering on the exchange.

### 4. Missing important features (UI)
- Account transactions history
- Position history
- PnL chart/current equity
- About 500 usdt cash is missing from paper account during 5 days without trades (?) but with orders (sell orders on spot maybe eat cash?)
- References to positions are required in history items