# Code Review: `015-live-binance-connector`

**6 commits, 65 files changed, +2657 / -359 lines**

This PR adds live Binance trading connectivity: Domain abstractions (`ILiveConnector`, `ILiveAccountManager`), Application command/query handlers, Infrastructure Binance client (REST + WebSocket), WebApi endpoints, and a CQRS refactor of existing query handlers.

---

## Critical Issues

### C1. `LiveSessionConfig.InitialCash` is `decimal` — violates Int64 Money Convention
`src/AlgoTradeForge.Domain/Live/LiveSessionConfig.cs`

`InitialCash` is `decimal` in Domain, but the constitution mandates `long` for all Domain money types. `BacktestOptions.InitialCash` is `long`. The scaling should happen in the Application layer (the conversion boundary), not Domain.

**Status: FIXED**

### C2. No auth on live trading endpoints
`src/AlgoTradeForge.WebApi/Endpoints/LiveEndpoints.cs`

The `StartSession` endpoint allows anyone to start a live trading session with real money — no `.RequireAuthorization()`. Critical for a system placing real Binance orders.

**Status: DEFERRED** — will address in a dedicated auth feature.

### C3. Race condition in execution report handling — partial fills & missing statuses
`src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceLiveConnector.cs` ~L280-300

- Only `"FILLED"` removes the pending order. No `PartiallyFilled` variant in `OrderStatus`.
- `"CANCELED"` and `"REJECTED"` execution types are silently ignored — a Binance order rejection via WebSocket would be lost.

**Status: FIXED**

### C4. API secret stored as plain `byte[]` for connector lifetime
`src/AlgoTradeForge.Infrastructure/Live/Binance/BinanceApiClient.cs` L13-17

Secret bytes persist in memory for the entire connector lifetime. Consider zeroing in `Dispose()` or accepting a signing delegate instead of raw secret material.

**Status: DEFERRED** — will address in a dedicated security hardening pass.

---

## Major Issues

### M1. `BinanceLiveOptions` with API keys in Application layer
`src/AlgoTradeForge.Application/Live/BinanceLiveOptions.cs`

Binance-specific config (REST URL, WS URL, API credentials) belongs in Infrastructure. Application should define a generic exchange abstraction. The TODO in the file acknowledges this.

**Status: FIXED** — Moved `BinanceLiveOptions` and `BinanceAccountConfig` to `Infrastructure/Live/Binance/`. Default options registration moved to Infrastructure DI.

### M2. `LiveOrderContext` depends on concrete `BinanceApiClient`
`src/AlgoTradeForge.Infrastructure/Live/LiveOrderContext.cs`

Takes a concrete `BinanceApiClient` — impossible to reuse for other exchanges. Should depend on an `IExchangeOrderClient` interface.

**Status: FIXED** — Created `IExchangeOrderClient` interface in Application layer. `BinanceApiClient` implements it. `LiveOrderContext` now depends on the interface.

### M3. Lock held during strategy callbacks — fragile
`BinanceLiveConnector.cs` ~L210-230, ~L270-310

`entry.Lock` is held while calling `strategy.OnBarComplete()` and `strategy.OnTrade()`. If user strategy code does anything blocking, this deadlocks. Consider a command queue pattern instead.

**Status: FIXED** — Replaced `Lock` with a per-session `Channel<Action>` event queue. Bar events, trade fills, and order terminations are enqueued and processed sequentially by a dedicated task per session. No lock held during strategy callbacks.

### M4. Volume divided by `tickSize` — wrong
`BinanceLiveConnector.cs` ~L200-210

```csharp
(long)(decimal.Parse(msg.Kline.Volume, ...) / tickSize)
```

Volume is quantity, not price. Dividing by tick size (e.g., 0.01) inflates volume 100x.

**Status: FIXED** — Changed to `(long)decimal.Parse(msg.Kline.Volume, ...)` without tickSize division.

### M5. `StopAsync` may not flush pending cancel requests
`BinanceLiveConnector.cs` ~L175-200

Cancel orders are queued to the channel, then `_cts.Cancel()` is called immediately. The `ProcessCancelsAsync` loop may exit before processing them, leaving orders open on Binance.

**Status: FIXED** — Reordered `LiveOrderContext.StopAsync()` to complete channels and await processing tasks before cancelling CTS. Queued orders/cancels now drain fully before shutdown.

### M6. `BinanceWebSocketManager.DisposeAsync` doesn't await `ReadTask`
`BinanceWebSocketManager.cs` ~L130-155

After closing sockets, the `ReadLoop` tasks are never awaited — can cause `ObjectDisposedException`.

**Status: FIXED** — `DisposeAsync` now closes sockets, awaits all read tasks, then disposes sockets in three separate passes.

### M7. Connector never disposed when last session removed
`StopLiveSessionCommandHandler.cs`

When the last session is removed, the connector keeps running (WebSocket, listen key timer, etc.) indefinitely. No cleanup logic for zero-session connectors.

**Status: FIXED** — Added `TryRemoveAsync` to `ILiveAccountManager`. `StopLiveSessionCommandHandler` now checks `SessionCount == 0` after removal and auto-disposes the connector via account manager.

---

## Minor Issues

- **m1.** `InMemoryLiveSessionStore.Add` silently ignores duplicate session IDs (`TryAdd` return value discarded)
- **m2.** `Order.Status` has `internal set` now accessible from Infrastructure via `InternalsVisibleTo` — widens trust boundary beyond Domain
- **m3.** `appsettings.json` has empty `ApiKey`/`ApiSecret` placeholders — risk of accidental commit; rely solely on user secrets
- **m4.** Live events (`LiveSessionStartEvent`, etc.) are dead code — `NullEventBus.Instance` is always used
- **m5.** `OnBarStart` fires simultaneously with `OnBarComplete` (only on closed klines) — misleading semantics; should fire when a new bar period begins
- **m6.** `PassthroughIndicatorFactory` used for live strategies — no indicator warmup means strategies relying on EMA/RSI will get incorrect values initially
- **m7.** ~~`SemaphoreSlim` in `BinanceLiveAccountManager` never disposed when accounts are removed — minor leak~~ **FIXED** — `TryRemoveAsync` now evicts the semaphore
- **m8.** `ParseRouting` treats `"None"` the same as omitting the field — potentially confusing

---

## Test Coverage Gaps

**Tested well**: Session store, command handlers, HMAC signing, message parsing, `LiveOrderContext` state, endpoint integration.

**Missing**:
- `BinanceLiveConnector` — the core orchestrator (no tests at all)
- `BinanceWebSocketManager` — reconnection, read loop, disposal
- Order processing pipeline (`ProcessOrdersAsync`, `ProcessCancelsAsync`)
- Partial fill scenarios
- Full live flow integration test (start → kline → order → execution → stop)

---

## Positive Aspects

- **CQRS refactor** of query handlers (`IQuery`/`IQueryHandler`) is clean and consistent
- **Channel-based order processing** in `LiveOrderContext` is excellent — decouples strategy from async HTTP
- **Per-account semaphores** with double-checked locking in `BinanceLiveAccountManager` is well-designed
- **Session isolation** — each session gets its own portfolio via `LiveOrderContext`
- **C# 14 `Lock` type** used at correct granularity (per session entry)
- **Exponential backoff** on WebSocket reconnection
- **User Secrets** integration for credential management
- Test quality for existing tests is high (concurrency, error states, rekeying)
