---
description: Implement a new exchange connector (REST + WebSocket + reconciliation)
handoffs:
  - /agent.debug
---

## User Input

```text
$ARGUMENTS
```

## Instructions

You are implementing a new exchange connector for the AlgoTradeForge live trading system. Connectors handle REST order placement, WebSocket market data + user data streams, order lifecycle management, reconciliation, and graceful shutdown. The Binance connector is the reference implementation.

### 1. Read Constitution

Read `.specify/memory/constitution.md` for code style rules. Key conventions:

- File-scoped namespaces
- Primary constructors where possible
- Int64 money convention (`long` for all Domain prices/monetary values)
- No XML doc comments (use `//` only where logic isn't self-evident)
- xUnit + NSubstitute for tests
- Serilog for logging

### 2. Search Exchange API Documentation

Use `WebSearch` to find the latest official REST and WebSocket API documentation for the target exchange. Extract:

| Field | Description |
|---|---|
| **Exchange name** | e.g. `Bybit`, `OKX`, `Kraken` |
| **REST base URL** | Production and testnet/sandbox URLs |
| **WebSocket market URL** | Kline/candlestick streams |
| **WebSocket user data URL** | Order fills, account updates |
| **Auth method** | HMAC-SHA256 (like Binance), Ed25519, RSA, etc. |
| **Rate limits** | Orders/sec, requests/min, weight system |
| **Testnet/Paper trading** | Does the exchange offer a testnet or sandbox? URLs? |

### 3. Parse User Input

From the user input, extract:

| Field | Required | Description |
|---|---|---|
| **Exchange** | Yes | Exchange name (e.g. `Bybit`) |
| **Scope** | No | `full` (default) or `market-data-only` |
| **Features** | No | Specific features like `futures`, `margin`, `spot-only` |

### 4. Clarify Gaps

If any of the following are unclear, **ask the user before proceeding**:

- Which product? (spot, USDM futures, COIN-M futures)
- Which order types must be supported? (market, limit, stop, stop-limit)
- Are there specific rate limit constraints to handle?
- Do they need multiple account support (like Binance's `Accounts` dict)?
- Should the connector handle its own reconnection or delegate to a shared manager?

### 5. Implementation Checklist

Work through these phases in order. Each phase builds on the previous.

#### Phase 1: Models (`{Exchange}Models.cs`)

- [ ] API response DTOs for: order placement result, execution report, kline/candle message, exchange info, account info, open orders query
- [ ] Parse decimals with `CultureInfo.InvariantCulture` (never `decimal.Parse(s)` without culture)
- [ ] Match field names to the exchange's JSON response format
- [ ] Use `System.Text.Json` attributes (`[JsonPropertyName]`) for serialization

Reference: `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceModels.cs`

#### Phase 2: API Client (`{Exchange}ApiClient.cs`)

- [ ] HTTP client with authentication (HMAC signing, API key headers)
- [ ] Time synchronization with exchange server (timestamp drift correction)
- [ ] Implement `IExchangeOrderClient` interface:
  - `PlaceOrderAsync` — place order, return `ExchangeOrderResult` with fills
  - `CancelOrderAsync` — cancel single order
  - `GetOpenOrdersAsync` — list open orders for symbol
  - `CancelAllOpenOrdersAsync` — cancel all open orders for symbol
- [ ] Additional methods: `GetTickerPriceAsync`, `GetExchangeInfoAsync`, `GetAccountInfoAsync`, `GetKlinesAsync`, `GetMyTradesAsync`
- [ ] Rate limit handling (delay/retry on 429)
- [ ] `IDisposable` for `HttpClient` cleanup

Reference: `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceApiClient.cs`

#### Phase 3: WebSocket Manager (`{Exchange}WebSocketManager.cs`)

- [ ] Market data streams (kline/candle subscriptions per symbol+interval)
- [ ] User data stream (execution reports, account updates)
- [ ] Automatic reconnection with configurable delay and max attempts
- [ ] Heartbeat/keepalive (ping/pong or subscription renewal)
- [ ] Thread-safe start/stop lifecycle
- [ ] Message parsing and callback dispatch

Reference: `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceWebSocketManager.cs`

#### Phase 4: Live Connector (`{Exchange}LiveConnector.cs`)

Implement `ILiveConnector` with these responsibilities:

- [ ] `ConnectAsync` — initialize API client, WebSocket manager, start reconciliation loop
- [ ] `AddSessionAsync` — validate balance, create `LiveOrderContext`, start EventQueue processing, subscribe to kline streams
- [ ] `RemoveSessionAsync` — cancel pending orders, drain EventQueue, stop order context
- [ ] `StopAsync` — cancel CTS, stop reconciliation, stop all sessions, safety-net cancel-all on exchange
- [ ] `DisposeAsync` — delegate to `StopAsync`

**Critical patterns to follow (from Binance implementation):**

| Pattern | Description |
|---|---|
| **EventQueue** | `Channel<Action>` per session with `SingleReader = true` — serializes all strategy callbacks |
| **Order routing** | `ConcurrentDictionary<long, Guid>` maps exchange order IDs to session IDs |
| **Buffered reports** | Queue execution reports arriving before order mapping completes |
| **REST fill dedup** | Track REST-filled orders to skip duplicate WebSocket fills |
| **Kline → Int64Bar** | Convert exchange kline data using `ScaleContext.FromMarketPrice()` |
| **Bar routing** | Enqueue `OnBarStart`/`OnBarComplete` via EventQueue, check `LiveEventRouting` flags |
| **Fill routing** | Enqueue `OnTrade` via EventQueue, update `OrderStatus`, manage pending orders |
| **3-phase reconciliation** | Snapshot expected orders on EventQueue → Detect on timer thread → Repair on EventQueue |

Reference: `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceLiveConnector.cs`

#### Phase 5: Order Context Integration

`LiveOrderContext` is exchange-agnostic. Wire it with:

- [ ] `Func<OrderType, string> mapOrderType` — maps `OrderType` enum to exchange order type strings
- [ ] `IExchangeOrderClient` — the Phase 2 API client
- [ ] Session-scoped `ConcurrentDictionary<long, Guid>` for order-to-session routing

Reference: `src/AlgoTradeForge.Infrastructure/Live/LiveOrderContext.cs`

#### Phase 6: Reconciliation

`OrderGroupReconciler` is exchange-agnostic and reusable. Wire it:

- [ ] Pass your `{Exchange}ApiClient` (which implements `IExchangeOrderClient`)
- [ ] Use the 3-phase pattern in the reconciliation loop:
  1. Snapshot: enqueue `GetExpectedOrders()` on EventQueue → await `TaskCompletionSource`
  2. Detect: `reconciler.DetectAsync(symbol, expected, resolveExchangeId, pendingIds, ct)`
  3. Repair: enqueue `RepairGroup()` calls on EventQueue + `CancelOrphansAsync()` directly

Reference: `src/AlgoTradeForge.Infrastructure/Live/OrderGroupReconciler.cs`

#### Phase 7: Configuration

- [ ] `{Exchange}LiveOptions` — shared options (reconnect delay, max attempts, reconciliation interval)
- [ ] `{Exchange}AccountConfig` — per-account config (REST URL, WS URL, API key, API secret)
- [ ] Support testnet/sandbox URLs alongside production

Reference: `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceLiveOptions.cs`

#### Phase 8: Safety

- [ ] Graceful shutdown: `StopAsync` cancels all pending orders via `Cancel()`, then safety-net `CancelAllOpenOrdersAsync` per symbol
- [ ] WebSocket heartbeat/keepalive to prevent disconnection
- [ ] CTS propagation: single `CancellationTokenSource` controls all background tasks
- [ ] Error isolation: catch exceptions in per-session callbacks, log and continue

### 6. File Layout

```
src/AlgoTradeForge.Infrastructure/Live/{Exchange}/
  {Exchange}Models.cs
  {Exchange}ApiClient.cs
  {Exchange}WebSocketManager.cs
  {Exchange}LiveConnector.cs
  {Exchange}LiveOptions.cs

tests/AlgoTradeForge.Infrastructure.Tests/Live/{Exchange}/
  {Exchange}ApiClientTests.cs
  {Exchange}LiveConnectorTests.cs
```

### 7. Write Tests

#### API Client Unit Tests (`{Exchange}ApiClientTests.cs`)

- [ ] Signing produces correct HMAC signature for known inputs
- [ ] Order type mapping covers all supported types
- [ ] Response parsing handles all expected JSON formats
- [ ] Time sync adjusts timestamp offset correctly

#### Connector Tests (`{Exchange}LiveConnectorTests.cs`)

- [ ] Multiple sessions can be added to one connector
- [ ] Fills route to the correct session
- [ ] `StopAsync` cancels all pending orders
- [ ] Reconciliation loop calls `DetectAsync` for `ITradeRegistryProvider` sessions

#### Testnet Integration Tests (optional, env-gated)

If the exchange has a testnet, create:

```
tests/AlgoTradeForge.Infrastructure.Tests/Live/Testnet/{Exchange}/
  {Exchange}TestnetCredentials.cs
  {Exchange}TestnetFixture.cs
  {Exchange}ConnectorOrderTests.cs
```

Pattern: skip tests when env vars are missing, use `Assert.Skip()`.

### 8. Build & Run Tests

```bash
# 1. Build entire solution
dotnet build AlgoTradeForge.slnx

# 2. Run new connector tests
dotnet test tests/AlgoTradeForge.Infrastructure.Tests --filter "FullyQualifiedName~{Exchange}"

# 3. Run full infra test suite
dotnet test tests/AlgoTradeForge.Infrastructure.Tests
```

### 9. Known Gaps & Resilience Considerations

Reference `docs/live-connector-binance.md` for resilience patterns that apply to all connectors:

- WebSocket reconnection edge cases (message loss during reconnect)
- Exchange-side order expiry (GTC vs IOC vs FOK)
- Partial fill handling across REST and WebSocket paths
- Rate limit backoff strategies
- Clock drift and timestamp rejection

### 10. Summary

Print a summary of what was created:

```
Connector: {Exchange}LiveConnector
Product: {Spot | Futures | ...}
Auth: {HMAC-SHA256 | Ed25519 | ...}
Testnet: {URL or "not available"}

Files created:
  src/.../Live/{Exchange}/{Exchange}Models.cs
  src/.../Live/{Exchange}/{Exchange}ApiClient.cs
  src/.../Live/{Exchange}/{Exchange}WebSocketManager.cs
  src/.../Live/{Exchange}/{Exchange}LiveConnector.cs
  src/.../Live/{Exchange}/{Exchange}LiveOptions.cs
  tests/.../{Exchange}ApiClientTests.cs
  tests/.../{Exchange}LiveConnectorTests.cs

Order types supported: Market, Limit, Stop, StopLimit
Interfaces implemented: ILiveConnector, IExchangeOrderClient
Reconciliation: Reuses OrderGroupReconciler (3-phase EventQueue pattern)

Tests: {count} passed
```

---

## Reference: Type-to-File Mapping

| Type | File |
|---|---|
| `ILiveConnector` | `src/AlgoTradeForge.Domain/Live/ILiveConnector.cs` |
| `IExchangeOrderClient` | `src/AlgoTradeForge.Application/Live/IExchangeOrderClient.cs` |
| `ExchangeOrderResult` | `src/AlgoTradeForge.Application/Live/IExchangeOrderClient.cs` |
| `ExchangeOpenOrder` | `src/AlgoTradeForge.Application/Live/IExchangeOrderClient.cs` |
| `LiveOrderContext` | `src/AlgoTradeForge.Infrastructure/Live/LiveOrderContext.cs` |
| `OrderGroupReconciler` | `src/AlgoTradeForge.Infrastructure/Live/OrderGroupReconciler.cs` |
| `LiveSessionConfig` | `src/AlgoTradeForge.Domain/Live/LiveSessionConfig.cs` |
| `LiveSessionStatus` | `src/AlgoTradeForge.Domain/Live/LiveSessionStatus.cs` |
| `LiveEventRouting` | `src/AlgoTradeForge.Domain/Live/LiveEventRouting.cs` |
| `ITradeRegistryProvider` | `src/AlgoTradeForge.Domain/Strategy/Modules/TradeRegistry/ITradeRegistryProvider.cs` |
| `TradeRegistryModule` | `src/AlgoTradeForge.Domain/Strategy/Modules/TradeRegistry/TradeRegistryModule.cs` |
| `ExpectedOrder` | `src/AlgoTradeForge.Domain/Strategy/Modules/TradeRegistry/ExpectedOrder.cs` |
| `IOrderValidator` | `src/AlgoTradeForge.Domain/Engine/IOrderValidator.cs` |
| `OrderValidator` | `src/AlgoTradeForge.Domain/Engine/OrderValidator.cs` |
| `Portfolio` | `src/AlgoTradeForge.Domain/Engine/Portfolio.cs` |
| `ScaleContext` | `src/AlgoTradeForge.Domain/ScaleContext.cs` |
| `Int64Bar` | `src/AlgoTradeForge.Domain/History/Int64Bar.cs` |
| `DataSubscription` | `src/AlgoTradeForge.Domain/History/DataSubscription.cs` |
| `BinanceLiveConnector` (reference) | `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceLiveConnector.cs` |
| `BinanceApiClient` (reference) | `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceApiClient.cs` |
| `BinanceWebSocketManager` (reference) | `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceWebSocketManager.cs` |
| `BinanceModels` (reference) | `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceModels.cs` |
| `BinanceLiveOptions` (reference) | `src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceLiveOptions.cs` |
| Resilience considerations | `docs/live-connector-binance.md` |
| Constitution | `.specify/memory/constitution.md` |
