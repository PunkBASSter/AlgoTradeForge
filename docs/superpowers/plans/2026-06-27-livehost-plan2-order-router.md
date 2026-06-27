# LiveHost Plan 2 — `IOrderRouter` + multi-account routing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** De-conflate the LiveHost order side into account-keyed seams (`IAccountTarget` + per-session `SessionOrderContext` facade, `IOrderRouter`, thin `IMarketDataSource`) so one market-data transport can fan orders to multiple isolated broker accounts, each scaling off its own asset — with Binance as the degenerate 1:1 case and a fake-venue two-account test as the acceptance.

**Architecture:** `BinanceLiveConnector` becomes a composition root owning one `IMarketDataSource` (thin wrapper over the existing `IStrategyDispatch` + `ITickRouter`) and one `IOrderRouter`. The router get-or-creates account-scoped `IAccountTarget`s via a venue-neutral `IAccountTargetFactory`; each target owns one shared broker-true `Portfolio` + account-scoped `LiveOrderContext`; strategies bind to a thin per-session `SessionOrderContext` facade that tags the originating session for `OnTrade` routing. Targets are born-running (`IAsyncDisposable`, reference-counted, disposed on last-session-leave). Per-order/per-instrument scaling uses `new ScaleContext(asset)` directly (zero-alloc struct — no cache).

**Tech Stack:** C# 14 / .NET 10, xUnit + NSubstitute, `System.Threading.Channels`, Serilog/`Microsoft.Extensions.Logging`.

**Design spec:** `docs/superpowers/specs/2026-06-27-livehost-plan2-order-router-design.md`

## Global Constraints

- **Test-First (Constitution II):** every task writes a failing xUnit test before implementation. NSubstitute for mocks.
- **One type per file**, named after the type. Single-line accompanying records may share the interface file.
- **No `Async` suffix** on new/updated async methods. **No sync-over-async** (`.Result`/`.Wait()`/`.GetAwaiter().GetResult()`) at prod call sites.
- **I/O-bound APIs async** with `CancellationToken ct = default`.
- **`using` over try/finally** for pure release; `SemaphoreSlim` via `using var _ = await gate.LockAsync(ct);` (`AlgoTradeForge.Storage.Threading.SemaphoreSlimExtensions`).
- **No `catch when (ex is not OperationCanceledException)`** in long-running loops — use `BinanceLiveConnector.IsTrueShutdown(ex, ct)`.
- **Int64 money:** `new ScaleContext(asset)` at the Application/Infrastructure boundary; never raw `(long)` for money. `ScaleContext` is a `readonly record struct` (zero heap alloc).
- **ONE `dotnet` process at a time.** Build: `dotnet build AlgoTradeForge.slnx`. Test: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/` (and `.Application.Tests` / `.WebApi.Tests` where noted). Shell is `powershell.exe` (no `pwsh`); avoid `cd /d` in bash.
- **Commits:** the implementer is hook-denied from `git add`; the **CONTROLLER** stages + commits after reviewing each task's diff. The `git` step in each task is the controller's action. Commit-message trailers: `Co-Authored-By:` + `Claude-Session:`.
- Branch: `feat/livehost-plan2-order-router` (already created off `main`).

---

## File Structure

**New — interfaces (`src/AlgoTradeForge.LiveHost.Application/Live/`):**
- `IAccountTarget.cs` — account-scoped order seam (`IAsyncDisposable`, born-running).
- `IOrderRouter.cs` — session→target binding + inbound report routing + refcount (`IAsyncDisposable`).
- `IAccountTargetFactory.cs` — venue-neutral target creation (discovers funds).
- `IMarketDataSource.cs` — thin transport-scoped data-plane seam.

**New — impls (`src/AlgoTradeForge.LiveHost.Infrastructure/Live/`):**
- `InstrumentScaleMap.cs` — static `Build(subscriptions) → Dictionary<string,ScaleContext>`.
- `SessionOrderContext.cs` — per-session `IOrderContext` facade over a shared `LiveOrderContext`.
- `AccountTarget.cs` — owns `Portfolio` + account `LiveOrderContext` + `IExchangeOrderClient`.
- `OrderRouter.cs` — `IOrderRouter` impl (per-account async gate + refcount + order→session map).
- `Binance/BinanceAccountTargetFactory.cs` — `IAccountTargetFactory` for Binance.
- `Binance/BinanceMarketDataSource.cs` — `IMarketDataSource` wrapping `IStrategyDispatch` + `ITickRouter`.

**Refactored:**
- `Infrastructure/Live/LiveOrderContext.cs` — account-scoped: drop `_primaryAsset` + `_sessionId` + `exchangeOrderToSession`; scale per `order.Asset`; add session-tagged submit/cancel + `OrderMapped(long, Guid)` event.
- `Infrastructure/Live/Binance/BinanceLiveConnector.cs` — composition root.
- `Application/Live/StartLiveSessionCommand.cs`, `Domain/Live/LiveSessionConfig.cs`, `WebApi/Contracts/StartLiveSessionRequest.cs`, `Application/Live/StartLiveSessionCommandHandler.cs` — drop `InitialCash`.
- `WebApi/LiveHostServiceCollectionExtensions.cs` — register new services.

**New tests (`tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/`):**
- `InstrumentScaleMapTests.cs`, `SessionOrderContextTests.cs`, `AccountTargetTests.cs`, `OrderRouterTests.cs`, `MultiAccountRoutingTests.cs`.
- Modify `LiveOrderContextTests.cs` (ctor change + per-asset scaling test).

---

## Task 1: Per-asset order scaling — drop `_primaryAsset` from `LiveOrderContext`

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveOrderContext.cs` (ctor + line ~225)
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs:281-283` (call site)
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/LiveOrderContextTests.cs`

**Interfaces:**
- Produces: `LiveOrderContext(Portfolio portfolio, IOrderValidator orderValidator, ILogger logger, IExchangeOrderClient orderClient, Guid sessionId, ConcurrentDictionary<long,Guid> exchangeOrderToSession, int channelCapacity = 1024)` — `Asset primaryAsset` parameter removed; orders scale off `order.Asset`.

- [ ] **Step 1: Write the failing test** (append to `LiveOrderContextTests.cs`)

```csharp
[Fact]
public async Task ProcessOrders_ScalesPrice_OffOrderAsset_NotAConstant()
{
    // EthUsdt has a coarser tick (0.01) than a hypothetical 8-dp asset; the order's OWN
    // asset must drive scaling. Build the context, submit a LIMIT order, capture the price.
    var ethUsdt = CryptoAsset.Create("ETHUSDT", "Binance",
        decimalDigits: 2, minOrderQuantity: 0.0001m, maxOrderQuantity: 9000m, quantityStepSize: 0.0001m);

    var client = Substitute.For<IExchangeOrderClient>();
    client.PlaceOrderAsync(Arg.Any<string>(), Arg.Any<OrderSide>(), Arg.Any<OrderType>(),
            Arg.Any<decimal>(), Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>())
        .Returns(new ExchangeOrderResult(7777L, []));

    var portfolio = new Portfolio { InitialCash = 100_000_00L };
    portfolio.Initialize();
    var ctx = new LiveOrderContext(
        portfolio, new OrderValidator(), NullLogger.Instance, client,
        Guid.NewGuid(), new ConcurrentDictionary<long, Guid>());
    ctx.Start(CancellationToken.None);

    // LimitPrice is tick-denominated: 3000.00 ETH at 0.01 tick = 300000 ticks.
    var order = new Order { Id = 0, Asset = ethUsdt, Side = OrderSide.Buy,
        Type = OrderType.Limit, Quantity = 0.01m, LimitPrice = 300000L };
    ctx.Submit(order);

    // Wait for the single-reader order task to drain.
    await Assert.EventuallyAsync(async () =>
    {
        await client.Received().PlaceOrderAsync("ETHUSDT", OrderSide.Buy, OrderType.Limit,
            0.01m, 3000.00m, null, Arg.Any<CancellationToken>());
    });
}
```

> If the codebase has no `Assert.EventuallyAsync` helper, poll with a bounded loop:
> `for (int i = 0; i < 50 && client.ReceivedCalls().Count() == 0; i++) await Task.Delay(20);`
> then assert. Use whichever matches the existing test conventions in this project (check a sibling test that awaits a channel-drained side effect, e.g. `BoundedChannelSafetyTests`).

- [ ] **Step 2: Run test to verify it fails to compile** (ctor still has `primaryAsset`)

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~ProcessOrders_ScalesPrice_OffOrderAsset"`
Expected: BUILD FAILURE — `LiveOrderContext` constructor has 8 params incl. `Asset`.

- [ ] **Step 3: Remove `_primaryAsset` and scale off `order.Asset`**

In `LiveOrderContext.cs`: delete the `private readonly Asset _primaryAsset;` field; remove the `Asset primaryAsset` ctor parameter and its assignment. In `ProcessOrdersAsync`, change:

```csharp
var scale = new ScaleContext(_primaryAsset);
```
to:
```csharp
var scale = new ScaleContext(order.Asset);
```

- [ ] **Step 4: Fix the connector call site** (`BinanceLiveConnector.cs:281-283`)

```csharp
var orderContext = new LiveOrderContext(
    portfolio, _orderValidator, _logger, _apiClient!,
    config.SessionId, _binanceOrderToSession, _sharedOptions.LiveChannelCapacity);
```

- [ ] **Step 5: Fix the existing test fixture** (`LiveOrderContextTests.cs` `CreateContext`)

```csharp
return new LiveOrderContext(
    portfolio, new OrderValidator(),
    NullLogger.Instance, apiClient,
    Guid.NewGuid(), new ConcurrentDictionary<long, Guid>());
```

- [ ] **Step 6: Run the full file’s tests**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~LiveOrderContextTests"`
Expected: PASS (all existing + the new scaling test).

- [ ] **Step 7: Commit** (controller)

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveOrderContext.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/LiveOrderContextTests.cs
git commit -m "refactor(livehost): scale live orders off order.Asset, drop LiveOrderContext primaryAsset"
```

---

## Task 2: `InstrumentScaleMap` — per-instrument data-plane scaling

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InstrumentScaleMap.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InstrumentScaleMapTests.cs`

**Interfaces:**
- Produces: `static IReadOnlyDictionary<string, ScaleContext> InstrumentScaleMap.Build(IReadOnlyList<DataFeedSubscription> subscriptions)` — keyed by `subscription.AssetName` (the data-plane instrument key, per `SessionSnapshotBars`), value `new ScaleContext(subscription.Asset!)`.

- [ ] **Step 1: Write the failing test**

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class InstrumentScaleMapTests
{
    [Fact]
    public void Build_KeysByAssetName_WithEachInstrumentsOwnScale()
    {
        var btc = CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);
        var eth = CryptoAsset.Create("ETHUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.0001m, maxOrderQuantity: 9000m, quantityStepSize: 0.0001m);

        var subs = new List<DataFeedSubscription>
        {
            new TickSubscription("BTCUSDT", "Binance") { Asset = btc },
            new TickSubscription("ETHUSDT", "Binance") { Asset = eth },
        };

        var map = InstrumentScaleMap.Build(subs);

        Assert.Equal(new ScaleContext(btc).TickSize, map["BTCUSDT"].TickSize);
        Assert.Equal(new ScaleContext(eth).TickSize, map["ETHUSDT"].TickSize);
    }
}
```

> Confirm `TickSubscription`'s constructor shape against `src/AlgoTradeForge.Domain/Strategy/Subscriptions/TickSubscription.cs` before running; it derives from `DataFeedSubscription(string AssetName, string Exchange, DataFeedRole Role)`. If `Role` is required positionally, pass the appropriate `DataFeedRole` value.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~InstrumentScaleMapTests"`
Expected: BUILD FAILURE — `InstrumentScaleMap` does not exist.

- [ ] **Step 3: Implement `InstrumentScaleMap`**

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Per-instrument price/qty scale for the data plane. Keyed by AssetName — the instrument
// key the dispatch/tick-router use (see SessionSnapshotBars). Replaces the single-asset
// "scale everything off the session asset" shortcut.
public static class InstrumentScaleMap
{
    public static IReadOnlyDictionary<string, ScaleContext> Build(
        IReadOnlyList<DataFeedSubscription> subscriptions)
    {
        var map = new Dictionary<string, ScaleContext>(StringComparer.Ordinal);
        foreach (var sub in subscriptions)
        {
            if (sub.Asset is null)
                throw new InvalidOperationException(
                    $"Subscription for '{sub.AssetName}' has no resolved Asset; resolve before building scales.");
            map[sub.AssetName] = new ScaleContext(sub.Asset);
        }
        return map;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~InstrumentScaleMapTests"`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InstrumentScaleMap.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InstrumentScaleMapTests.cs
git commit -m "feat(livehost): add InstrumentScaleMap for per-instrument data-plane scaling"
```

---

## Task 3: Account-scope `LiveOrderContext` — session-tagged submit + `OrderMapped(long, Guid)`

This makes one `LiveOrderContext` serve many sessions: the order→session tag moves off the
single `_sessionId` field onto per-order tagging, and re-key raises a `(exchangeId, sessionId)`
event the router subscribes to. The public `IOrderContext` surface moves to the facade (Task 4),
so `LiveOrderContext` stops implementing `IOrderContext` and exposes session-tagged internals.

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveOrderContext.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/SessionOrderContext.cs` (the per-session facade — needed here so the connector still binds an `IOrderContext` once `LiveOrderContext` stops implementing it)
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs` (temporary call-site shim — fully replaced in Task 8)
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/LiveOrderContextTests.cs`, `Live/SessionOrderContextTests.cs`

**Interfaces:**
- Produces:
  - `LiveOrderContext(Portfolio portfolio, IOrderValidator orderValidator, ILogger logger, IExchangeOrderClient orderClient, int channelCapacity = 1024)` — `sessionId` + `exchangeOrderToSession` params removed.
  - `long Submit(Order order, Guid sessionId)` — tags the order's originating session.
  - `Order? Cancel(long orderId)` (unchanged signature; session-agnostic).
  - `event Action<long, Guid>? OrderMapped` — raised on re-key with `(exchangeOrderId, sessionId)`.
  - existing `Cash`/`UsedMargin`/`AvailableMargin`/`GetPendingOrders`/`GetFills`/`GetPositions`/`AddFill`/`Portfolio`/`ResolveExchangeOrderId`/`IsOrderRestFilled`/`GetPendingOrder`/`RemovePendingOrder`/`ClearRecentFills`/`GetAllFills` remain.

- [ ] **Step 1: Write the failing test** (append to `LiveOrderContextTests.cs`)

```csharp
[Fact]
public void OrderMapped_CarriesOriginatingSessionId_AfterRekey()
{
    var portfolio = new Portfolio { InitialCash = 100_000_00L };
    portfolio.Initialize();
    var ctx = new LiveOrderContext(
        portfolio, new OrderValidator(), NullLogger.Instance,
        Substitute.For<IExchangeOrderClient>());
    ctx.Start(CancellationToken.None);

    var sessionId = Guid.NewGuid();
    (long mappedExchangeId, Guid mappedSession)? captured = null;
    ctx.OrderMapped += (exId, sId) => captured = (exId, sId);

    var order = new Order { Id = 0, Asset = BtcUsdt, Side = OrderSide.Buy,
        Type = OrderType.Limit, Quantity = 0.001m, LimitPrice = 5000000L };
    var localId = ctx.Submit(order, sessionId);

    const long exchangeId = 4242L;
    ctx.RekeyToExchangeId(localId, exchangeId);

    Assert.Equal((exchangeId, sessionId), captured);
}
```

- [ ] **Step 2: Run to verify it fails to compile**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~OrderMapped_CarriesOriginatingSessionId"`
Expected: BUILD FAILURE — no `Submit(Order, Guid)` overload; `OrderMapped` is `Action<long>`.

- [ ] **Step 3: Refactor `LiveOrderContext` to account scope**

Apply these changes (full replacements of the affected members):

Remove fields/params: delete `private readonly Guid _sessionId;`, `private readonly ConcurrentDictionary<long, Guid> _exchangeOrderToSession;`, and remove both from the constructor + assignments. Stop declaring `: IOrderContext` on the class. Add:

```csharp
private readonly ConcurrentDictionary<long, Guid> _localToSession = new();

public new event Action<long, Guid>? OrderMapped;   // (exchangeOrderId, sessionId)
```

> The old `internal event Action<long>? OrderMapped;` and `internal void RekeyToExchangeId(long localId, long exchangeOrderId)` are replaced; remove the old event declaration entirely (no `new` needed once the old one is gone — the snippet above shows the final single declaration `public event Action<long, Guid>? OrderMapped;`).

Replace `Submit`:

```csharp
public long Submit(Order order, Guid sessionId)
{
    var rejection = _orderValidator.ValidateSubmission(order);
    if (rejection is not null)
    {
        order.Status = OrderStatus.Rejected;
        _logger.LogWarning("Order rejected: {Reason}", rejection);
        return order.Id;
    }

    var id = Interlocked.Increment(ref _nextOrderId);
    order.Id = id;
    order.SubmittedAt = DateTimeOffset.UtcNow;
    order.Status = OrderStatus.Pending;

    _pendingOrders.TryAdd(id, order);
    _localToSession.TryAdd(id, sessionId);

    if (!_orderChannel.Writer.TryWrite(new OrderRequest(order, id)))
    {
        order.Status = OrderStatus.Rejected;
        _pendingOrders.TryRemove(id, out _);
        _localToSession.TryRemove(id, out _);
        _logger.LogError(
            "Order channel full (capacity reached) — rejecting order {LocalId} ({Side} {Qty} {Asset})",
            id, order.Side, order.Quantity, order.Asset.Name);
    }
    return id;
}
```

Replace `RekeyToExchangeId` and the re-key block inside `ProcessOrdersAsync` to raise the 2-arg event using `_localToSession`:

```csharp
internal void RekeyToExchangeId(long localId, long exchangeOrderId)
{
    if (_pendingOrders.TryRemove(localId, out var order))
    {
        order.Id = exchangeOrderId;
        _pendingOrders.TryAdd(exchangeOrderId, order);
        _localToExchangeId.TryAdd(localId, exchangeOrderId);
        if (_localToSession.TryGetValue(localId, out var sId))
            OrderMapped?.Invoke(exchangeOrderId, sId);
    }
}
```

In `ProcessOrdersAsync`, where it re-keys after `PlaceOrderAsync` (the `if (pending is not null)` block), replace the `_exchangeOrderToSession.TryAdd(...)` + `OrderMapped?.Invoke(exchangeOrderId)` lines with:

```csharp
pending.Id = exchangeOrderId;
_pendingOrders.TryAdd(exchangeOrderId, pending);
_localToExchangeId.TryAdd(request.LocalId, exchangeOrderId);
if (_localToSession.TryGetValue(request.LocalId, out var pendingSession))
    OrderMapped?.Invoke(exchangeOrderId, pendingSession);
```

> Note: `_exchangeOrderToSession` references are now gone; the router records `exchangeOrderId → sessionId` by subscribing to `OrderMapped` (Task 5/8).

- [ ] **Step 4: Temporary connector shim** (keeps the build green until Task 8)

In `BinanceLiveConnector.AddSessionAsync`, update the `LiveOrderContext` construction to the new ctor and bridge the 2-arg event to the existing `_binanceOrderToSession` map + `DrainBufferedReports` so current E2E tests keep passing:

```csharp
var orderContext = new LiveOrderContext(
    portfolio, _orderValidator, _logger, _apiClient!, _sharedOptions.LiveChannelCapacity);
orderContext.Start(_cts!.Token);
orderContext.OrderMapped += (exchangeId, _) =>
{
    _binanceOrderToSession.TryAdd(exchangeId, config.SessionId);
    DrainBufferedReports(exchangeId);
};
```

Because `LiveOrderContext` no longer implements `IOrderContext`, bind the strategy to the new `SessionOrderContext` facade (created in Step 4b below):

```csharp
if (config.Strategy is IOrderContextReceiver orderReceiver)
    orderReceiver.SetOrderContext(new SessionOrderContext(config.SessionId, orderContext));
```

- [ ] **Step 4b: Create `SessionOrderContext` facade + its test**

`SessionOrderContext.cs`:

```csharp
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Per-session facade over a shared, account-scoped LiveOrderContext. Tags every Submit
// with its session id so the originating strategy gets OnTrade; all reads/state delegate
// to the shared account ledger.
public sealed class SessionOrderContext(Guid sessionId, LiveOrderContext account) : IOrderContext
{
    public long Cash => account.Cash;
    public long UsedMargin => account.UsedMargin;
    public long AvailableMargin => account.AvailableMargin;
    public long Submit(Order order) => account.Submit(order, sessionId);
    public Order? Cancel(long orderId) => account.Cancel(orderId);
    public IReadOnlyList<Order> GetPendingOrders() => account.GetPendingOrders();
    public IReadOnlyList<Fill> GetFills() => account.GetFills();
    public IReadOnlyDictionary<string, Position> GetPositions() => account.GetPositions();
}
```

`SessionOrderContextTests.cs`:

```csharp
using System.Collections.Concurrent;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class SessionOrderContextTests
{
    private static readonly CryptoAsset BtcUsdt = CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2, minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);

    [Fact]
    public void Submit_TagsOriginatingSession_OnSharedAccountContext()
    {
        var portfolio = new Portfolio { InitialCash = 100_000_00L };
        portfolio.Initialize();
        var account = new LiveOrderContext(portfolio, new OrderValidator(),
            NullLogger.Instance, Substitute.For<IExchangeOrderClient>());
        account.Start(CancellationToken.None);

        var sessionA = Guid.NewGuid();
        Guid? mappedSession = null;
        account.OrderMapped += (_, sId) => mappedSession = sId;

        IOrderContext facade = new SessionOrderContext(sessionA, account);
        var order = new Order { Id = 0, Asset = BtcUsdt, Side = OrderSide.Buy,
            Type = OrderType.Limit, Quantity = 0.001m, LimitPrice = 5000000L };
        var localId = facade.Submit(order);
        account.RekeyToExchangeId(localId, 99L);

        Assert.Equal(sessionA, mappedSession);
        Assert.Equal(portfolio.Cash, facade.Cash);  // reads delegate to the shared ledger
    }
}
```

- [ ] **Step 5: Update `LiveOrderContextTests` for the new ctor + `Submit(order, sessionId)`**

In `CreateContext`, drop the `Guid` + dictionary args:

```csharp
return new LiveOrderContext(
    portfolio, new OrderValidator(), NullLogger.Instance, apiClient);
```

Every existing `ctx.Submit(order)` call in this file becomes `ctx.Submit(order, Guid.NewGuid())`. The `OrderMapped_FiredAfterRekeyToExchangeId` test becomes the new `OrderMapped_CarriesOriginatingSessionId_AfterRekey` (delete the old one).

- [ ] **Step 6: Build + run the Infrastructure tests**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/`
Expected: PASS (LiveOrderContext + existing E2E via the bridge shim).

- [ ] **Step 7: Commit** (controller)

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveOrderContext.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/SessionOrderContext.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/LiveOrderContextTests.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/SessionOrderContextTests.cs
git commit -m "refactor(livehost): account-scope LiveOrderContext + SessionOrderContext facade"
```

---

## Task 4: `IAccountTarget` + `AccountTarget`

> `SessionOrderContext` (the per-session facade) is created in Task 3 — it is a prerequisite for Task 3's connector shim. This task adds the account-target seam around it.

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/IAccountTarget.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/AccountTarget.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/AccountTargetTests.cs`

**Interfaces:**
- Produces:
  - `interface IAccountTarget : IAsyncDisposable { string AccountName { get; } IOrderContext OrderContextFor(Guid sessionId); Portfolio Portfolio { get; } }`
  - `sealed class AccountTarget : IAccountTarget` — ctor `(string accountName, Portfolio portfolio, LiveOrderContext orderContext, IExchangeOrderClient orderClient, IEnumerable<string> symbolsToCancelOnDispose, ILogger logger)`; born running (caller `Start`s the context before handing it in, or `AccountTarget` calls `orderContext.Start(ct)` — see Step 3); `DisposeAsync` flushes + cancels-all + idempotent.
- Consumes: `SessionOrderContext` (Task 3), `LiveOrderContext` (Task 3).

- [ ] **Step 1: Write the failing test**

`AccountTargetTests.cs`:

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class AccountTargetTests
{
    private static AccountTarget CreateTarget(IExchangeOrderClient client, out Portfolio portfolio)
    {
        portfolio = new Portfolio { InitialCash = 50_000_00L };
        portfolio.Initialize();
        var ctx = new LiveOrderContext(portfolio, new OrderValidator(), NullLogger.Instance, client);
        ctx.Start(CancellationToken.None);
        return new AccountTarget("acctA", portfolio, ctx, client, ["BTCUSDT"], NullLogger.Instance);
    }

    [Fact]
    public void OrderContextFor_ReturnsFacade_OverSharedPortfolio()
    {
        var target = CreateTarget(Substitute.For<IExchangeOrderClient>(), out var portfolio);
        var ctx = target.OrderContextFor(Guid.NewGuid());
        Assert.Equal(portfolio.Cash, ctx.Cash);
        Assert.Same(portfolio, target.Portfolio);
    }

    [Fact]
    public async Task DisposeAsync_CancelsOpenOrders_AndIsIdempotent()
    {
        var client = Substitute.For<IExchangeOrderClient>();
        var target = CreateTarget(client, out _);

        await target.DisposeAsync();
        await target.DisposeAsync();   // second dispose is a no-op

        await client.Received(1).CancelAllOpenOrdersAsync("BTCUSDT", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~AccountTargetTests"`
Expected: BUILD FAILURE — `IAccountTarget`, `AccountTarget` do not exist.

- [ ] **Step 3: Implement the two types**

`IAccountTarget.cs`:

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IAccountTarget : IAsyncDisposable
{
    string AccountName { get; }
    IOrderContext OrderContextFor(Guid sessionId);
    Portfolio Portfolio { get; }
}
```

`AccountTarget.cs`:

```csharp
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// One broker account. Owns the shared Portfolio + account-scoped LiveOrderContext.
// Born running (the LiveOrderContext is Start()ed before/at construction); torn down via
// DisposeAsync (flush queued orders/cancels via LiveOrderContext.StopAsync, then cancel-all
// open orders on the exchange). Idempotent.
public sealed class AccountTarget : IAccountTarget
{
    private readonly LiveOrderContext _orderContext;
    private readonly IExchangeOrderClient _orderClient;
    private readonly IReadOnlyList<string> _symbolsToCancelOnDispose;
    private readonly ILogger _logger;
    private int _disposed;

    public string AccountName { get; }
    public Portfolio Portfolio { get; }

    public AccountTarget(
        string accountName,
        Portfolio portfolio,
        LiveOrderContext orderContext,
        IExchangeOrderClient orderClient,
        IEnumerable<string> symbolsToCancelOnDispose,
        ILogger logger)
    {
        AccountName = accountName;
        Portfolio = portfolio;
        _orderContext = orderContext;
        _orderClient = orderClient;
        _symbolsToCancelOnDispose = symbolsToCancelOnDispose.ToList();
        _logger = logger;
    }

    public IOrderContext OrderContextFor(Guid sessionId) => new SessionOrderContext(sessionId, _orderContext);

    internal LiveOrderContext OrderContext => _orderContext;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        // Graceful: flush queued orders/cancels first (StopAsync awaits the drain tasks).
        await _orderContext.StopAsync();

        foreach (var symbol in _symbolsToCancelOnDispose)
        {
            try { await _orderClient.CancelAllOpenOrdersAsync(symbol); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cancel-all on dispose failed for {Symbol} (account {Account})",
                    symbol, AccountName);
            }
        }
    }
}
```

> `Action<long, Guid>? OrderMapped` lives on `LiveOrderContext`; the router subscribes to it (Task 5/8) via `target.OrderContext.OrderMapped`. `OrderContext` is exposed `internal` for that wiring.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~AccountTargetTests"`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/IAccountTarget.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/AccountTarget.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/AccountTargetTests.cs
git commit -m "feat(livehost): add IAccountTarget + AccountTarget (born-running, IAsyncDisposable)"
```

---

## Task 5: `IAccountTargetFactory` + `IOrderRouter` + `OrderRouter`

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/IAccountTargetFactory.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/IOrderRouter.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/OrderRouter.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/OrderRouterTests.cs`

**Interfaces:**
- Produces:
  - `interface IAccountTargetFactory { Task<IAccountTarget> Create(string account, CancellationToken ct = default); }`
  - `interface IOrderRouter : IAsyncDisposable { Task<IAccountTarget> ResolveTarget(string account, CancellationToken ct = default); Task ReleaseTarget(string account, CancellationToken ct = default); void TrackOrder(long exchangeOrderId, Guid sessionId); bool TryResolveSession(long exchangeOrderId, out Guid sessionId); IReadOnlyCollection<IAccountTarget> Targets { get; } }`
  - `sealed class OrderRouter(IAccountTargetFactory factory, ILogger<OrderRouter> logger) : IOrderRouter`.
- Consumes: `IAccountTarget` (Task 4); `LiveOrderContext.OrderMapped` event (Task 3) — the router subscribes to it on the created target to populate `TrackOrder`.

- [ ] **Step 1: Write the failing tests**

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class OrderRouterTests
{
    private static IAccountTarget FakeTarget(string name)
    {
        var t = Substitute.For<IAccountTarget>();
        t.AccountName.Returns(name);
        return t;
    }

    [Fact]
    public async Task ResolveTarget_GetOrCreate_CreatesOncePerAccount_UnderConcurrency()
    {
        var factory = Substitute.For<IAccountTargetFactory>();
        factory.Create("A", Arg.Any<CancellationToken>()).Returns(_ => FakeTarget("A"));
        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        var tasks = Enumerable.Range(0, 16).Select(_ => router.ResolveTarget("A")).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Same(results[0], r));
        await factory.Received(1).Create("A", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReleaseTarget_DisposesTarget_OnlyOnLastRelease()
    {
        var target = FakeTarget("A");
        var factory = Substitute.For<IAccountTargetFactory>();
        factory.Create("A", Arg.Any<CancellationToken>()).Returns(target);
        var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

        await router.ResolveTarget("A");   // refcount 1
        await router.ResolveTarget("A");   // refcount 2 (same target)

        await router.ReleaseTarget("A");   // -> 1, not disposed
        await target.DidNotReceive().DisposeAsync();

        await router.ReleaseTarget("A");   // -> 0, disposed
        await target.Received(1).DisposeAsync();
        Assert.Empty(router.Targets);
    }

    [Fact]
    public void TrackOrder_Then_TryResolveSession_RoundTrips()
    {
        var router = new OrderRouter(Substitute.For<IAccountTargetFactory>(), NullLogger<OrderRouter>.Instance);
        var session = Guid.NewGuid();
        router.TrackOrder(123L, session);

        Assert.True(router.TryResolveSession(123L, out var resolved));
        Assert.Equal(session, resolved);
        Assert.False(router.TryResolveSession(999L, out _));
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~OrderRouterTests"`
Expected: BUILD FAILURE — types don’t exist.

- [ ] **Step 3: Implement the interfaces + `OrderRouter`**

`IAccountTargetFactory.cs`:

```csharp
namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IAccountTargetFactory
{
    Task<IAccountTarget> Create(string account, CancellationToken ct = default);
}
```

`IOrderRouter.cs`:

```csharp
namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IOrderRouter : IAsyncDisposable
{
    Task<IAccountTarget> ResolveTarget(string account, CancellationToken ct = default);
    Task ReleaseTarget(string account, CancellationToken ct = default);
    void TrackOrder(long exchangeOrderId, Guid sessionId);
    bool TryResolveSession(long exchangeOrderId, out Guid sessionId);
    IReadOnlyCollection<IAccountTarget> Targets { get; }
}
```

`OrderRouter.cs`:

```csharp
using System.Collections.Concurrent;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.Storage.Threading;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

public sealed class OrderRouter(IAccountTargetFactory factory, ILogger<OrderRouter> logger) : IOrderRouter
{
    private sealed class Entry(IAccountTarget target) { public readonly IAccountTarget Target = target; public int RefCount; }

    private readonly ConcurrentDictionary<string, Entry> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, Guid> _orderToSession = new();

    public IReadOnlyCollection<IAccountTarget> Targets =>
        _targets.Values.Select(e => e.Target).ToList();

    public async Task<IAccountTarget> ResolveTarget(string account, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        using var _ = await gate.LockAsync(ct);

        var entry = _targets.TryGetValue(account, out var existing)
            ? existing
            : _targets[account] = new Entry(await factory.Create(account, ct));

        entry.RefCount++;
        return entry.Target;
    }

    public async Task ReleaseTarget(string account, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(account, _ => new SemaphoreSlim(1, 1));
        using var _ = await gate.LockAsync(ct);

        if (!_targets.TryGetValue(account, out var entry))
            return;

        if (--entry.RefCount > 0)
            return;

        _targets.TryRemove(account, out _);
        try { await entry.Target.DisposeAsync(); }
        catch (Exception ex) { logger.LogError(ex, "Disposing target for account {Account} failed", account); }
    }

    public void TrackOrder(long exchangeOrderId, Guid sessionId) =>
        _orderToSession[exchangeOrderId] = sessionId;

    public bool TryResolveSession(long exchangeOrderId, out Guid sessionId) =>
        _orderToSession.TryGetValue(exchangeOrderId, out sessionId);

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _targets.Values)
        {
            try { await entry.Target.DisposeAsync(); }
            catch (Exception ex) { logger.LogError(ex, "Disposing target {Account} on router shutdown failed", entry.Target.AccountName); }
        }
        _targets.Clear();
    }
}
```

> `SemaphoreSlimExtensions.LockAsync` lives in `AlgoTradeForge.Storage.Threading` (per CLAUDE.md). Confirm the `using` namespace compiles; if the Infrastructure project lacks the reference, it is already used elsewhere in LiveHost — match an existing `using AlgoTradeForge.Storage.Threading;` site.

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~OrderRouterTests"`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/IAccountTargetFactory.cs \
        src/AlgoTradeForge.LiveHost.Application/Live/IOrderRouter.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/OrderRouter.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/OrderRouterTests.cs
git commit -m "feat(livehost): add IOrderRouter/OrderRouter with refcounted account targets"
```

---

## Task 6: `IMarketDataSource` + `BinanceMarketDataSource` (thin wrapper)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/IMarketDataSource.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceMarketDataSource.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/BinanceMarketDataSourceTests.cs`

**Interfaces:**
- Produces: `interface IMarketDataSource { void Register(LiveSessionRegistration registration); ValueTask EnsureSources(LiveSessionRegistration reg, Func<string, ScaleContext> scaleFor); IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec); ValueTask RemoveSources(Guid sessionId); }`
- Consumes: `IStrategyDispatch`, `ITickRouter` (existing `LiveHost.Application.Live.DataPlane`).

- [ ] **Step 1: Write the failing test**

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class BinanceMarketDataSourceTests
{
    [Fact]
    public async Task Delegates_RegisterAndEnsureSources_ToDispatchAndTickRouter()
    {
        var dispatch = Substitute.For<IStrategyDispatch>();
        var tickRouter = Substitute.For<ITickRouter>();
        var source = new BinanceMarketDataSource(dispatch, tickRouter);

        var reg = new LiveSessionRegistration(Guid.NewGuid(),
            Substitute.For<AlgoTradeForge.Domain.Strategy.IInt64BarStrategy>(),
            [], System.Threading.Channels.Channel.CreateUnbounded<Action>().Writer);

        source.Register(reg);
        Func<string, ScaleContext> scaleFor = _ => new ScaleContext(0.01m);
        await source.EnsureSources(reg, scaleFor);

        dispatch.Received(1).Register(reg);
        await tickRouter.Received(1).EnsureSources(reg, scaleFor);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~BinanceMarketDataSourceTests"`
Expected: BUILD FAILURE — types don’t exist.

- [ ] **Step 3: Implement**

`IMarketDataSource.cs`:

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IMarketDataSource
{
    void Register(LiveSessionRegistration registration);
    ValueTask EnsureSources(LiveSessionRegistration reg, Func<string, ScaleContext> scaleFor);
    IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec);
    ValueTask RemoveSources(Guid sessionId);
}
```

`BinanceMarketDataSource.cs`:

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;

// Thin transport-scoped data-plane seam: names the already-shared dispatch + tick router so
// multiple account targets can share one source. Plan 3's IbSession implements this for real.
public sealed class BinanceMarketDataSource(IStrategyDispatch dispatch, ITickRouter tickRouter) : IMarketDataSource
{
    public void Register(LiveSessionRegistration registration) => dispatch.Register(registration);

    public ValueTask EnsureSources(LiveSessionRegistration reg, Func<string, ScaleContext> scaleFor) =>
        tickRouter.EnsureSources(reg, scaleFor);

    public IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec) =>
        tickRouter.RecentBars(instrument, spec);

    public ValueTask RemoveSources(Guid sessionId) => tickRouter.RemoveSources(sessionId);
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~BinanceMarketDataSourceTests"`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/IMarketDataSource.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceMarketDataSource.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/BinanceMarketDataSourceTests.cs
git commit -m "feat(livehost): add IMarketDataSource thin wrapper (BinanceMarketDataSource)"
```

---

## Task 7: `BinanceAccountTargetFactory` (discovers funds from the broker)

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceAccountTargetFactory.cs`
- Test: covered indirectly by Task 8’s E2E + a focused funds-discovery unit test here.
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/BinanceAccountTargetFactoryTests.cs`

**Interfaces:**
- Produces:
  - `interface IAccountFundsSource { Task<long> GetFreeFundsScaled(Asset asset, CancellationToken ct = default); }` (`LiveHost.Application.Live`) — the substitutable funds-discovery seam.
  - `sealed class BinanceAccountTargetFactory(IAccountFundsSource funds, IOrderValidator orderValidator, ILogger logger, int channelCapacity, Func<IReadOnlyList<string>> symbolsForAccount, Func<Asset> assetForAccount) : IAccountTargetFactory` — `Create` calls `funds.GetFreeFundsScaled`, seeds `Portfolio.InitialCash` from it, builds a started `LiveOrderContext`, returns an `AccountTarget`.
- Consumes: `IAccountFundsSource` (new, Binance adapter over `BinanceApiClient.GetAccountInfoAsync`), `AccountTarget`, `LiveOrderContext`.

> **Implementation note for the executor:** `BinanceApiClient` is concrete (not an interface) and constructed inside `BinanceLiveConnector.ConnectAsync`. To keep the factory unit-testable, inject a small seam: define `interface IAccountFundsSource { Task<long> GetFreeFundsScaled(Asset asset, CancellationToken ct); }` implemented by a Binance adapter over `GetAccountInfoAsync`/`GetExchangeInfoAsync` + `ScaleContext`. The factory depends on `IAccountFundsSource` (substitutable) instead of the concrete client. Read `BinanceLiveConnector.AddSessionAsync:248-276` for the exact discovery + scaling logic to lift into the adapter.

- [ ] **Step 1: Write the failing test** (funds discovery seeds the portfolio)

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public class BinanceAccountTargetFactoryTests
{
    [Fact]
    public async Task Create_SeedsPortfolio_FromDiscoveredFreeFunds()
    {
        var funds = Substitute.For<IAccountFundsSource>();
        funds.GetFreeFundsScaled(Arg.Any<Asset>(), Arg.Any<CancellationToken>()).Returns(12_345_00L);

        var factory = new BinanceAccountTargetFactory(
            funds, new OrderValidator(), NullLogger.Instance, channelCapacity: 1024,
            symbolsForAccount: () => ["BTCUSDT"], assetForAccount: () => SomeBtcAsset());

        var target = await factory.Create("acctA");

        Assert.Equal(12_345_00L, target.Portfolio.InitialCash);
    }

    private static Asset SomeBtcAsset() => CryptoAsset.Create("BTCUSDT", "Binance",
        decimalDigits: 2, minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);
}
```

> The factory’s exact constructor shape (how it learns the account’s symbol/asset for discovery + cancel-on-dispose) is finalized against Task 8’s wiring — the connector knows the execution asset per session. The executor MAY adjust the `symbolsForAccount`/`assetForAccount` delegates to match how the connector supplies them; keep the discovered-funds-seeds-portfolio assertion intact.

- [ ] **Step 2: Run to verify it fails** — BUILD FAILURE (types don’t exist).

- [ ] **Step 3: Implement `IAccountFundsSource`, its Binance adapter, and `BinanceAccountTargetFactory`** lifting the discovery/scaling from `BinanceLiveConnector.AddSessionAsync:248-276`. The factory builds a `Portfolio { InitialCash = discovered }`, `Initialize()`s it, constructs + `Start`s a `LiveOrderContext`, and returns `new AccountTarget(account, portfolio, ctx, orderClient, symbols, logger)`.

- [ ] **Step 4: Run to verify it passes.**

- [ ] **Step 5: Commit** (controller)

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceAccountTargetFactory.cs \
        src/AlgoTradeForge.LiveHost.Application/Live/IAccountFundsSource.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/BinanceAccountTargetFactoryTests.cs
git commit -m "feat(livehost): BinanceAccountTargetFactory seeds portfolio from discovered funds"
```

---

## Task 8: Recompose `BinanceLiveConnector` as a composition root

This is the integration task. The connector keeps WS/user-data lifecycle, `ConnectAsync`,
the reconciliation timer, and a per-session registry (strategy + queues + subscriptions),
but delegates the order side to `IOrderRouter` + `IAccountTarget` and the data side to
`IMarketDataSource`. The existing `BinanceLiveConnectorE2ETests` are the behavior guard.

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs`
- Modify: `src/AlgoTradeForge.LiveHost.WebApi/LiveHostServiceCollectionExtensions.cs` (register `IOrderRouter`, `IMarketDataSource`, `IAccountTargetFactory`)
- Test: existing `BinanceLiveConnectorE2ETests.cs` (must stay green); update `BinanceLiveAccountManagerTests` if the connector ctor changes.

**Interfaces:**
- Consumes: `IMarketDataSource` (6), `IOrderRouter`/`OrderRouter` (5), `IAccountTargetFactory`/`BinanceAccountTargetFactory` (7), `InstrumentScaleMap` (2), `AccountTarget.OrderContext`/`OrderMapped` (3,4).
- Produces: a `BinanceLiveConnector` whose `AddSessionAsync` resolves a target, binds `target.OrderContextFor(sessionId)`, registers via the source with a per-instrument scale resolver, and tracks order→session via the router; `OnExecutionReport` routes via `router.TryResolveSession`; reconciliation iterates `router.Targets`; remove/stop `ReleaseTarget` + dispose the router/source.

- [ ] **Step 1: Write/keep the guard test** — confirm `BinanceLiveConnectorE2ETests` exercises add-session → order → execution-report → fill. If it constructs `BinanceLiveConnector` directly, it will need the new constructor dependencies (`IMarketDataSource`, `IOrderRouter`); update the fixture to pass `new BinanceMarketDataSource(dispatch, tickRouter)` and `new OrderRouter(new <fake-or-binance>Factory(...), logger)`. Add one new assertion: after `RemoveSessionAsync`, the account target is released (`router.Targets` empty for a single-session account).

- [ ] **Step 2: Run the E2E guard to verify it fails** against the un-refactored connector’s new wiring.

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~BinanceLiveConnectorE2E"`
Expected: BUILD FAILURE or assertion FAIL (router not wired).

- [ ] **Step 3: Recompose the connector.** Apply these concrete changes:

**(a) Constructor + fields.** Replace the injected `ITickRouter _tickRouter` + `IStrategyDispatch _dispatch` usage with `IMarketDataSource _source` and add `IOrderRouter _router`. Keep `_sessions` (strategy + queues + subscriptions + the bound `SessionOrderContext` + the resolved `AccountTarget`’s account name), but drop `_binanceOrderToSession` (router owns it). Constructor gains `IMarketDataSource source, IOrderRouter router`.

```csharp
public BinanceLiveConnector(
    string accountName,
    BinanceAccountConfig accountConfig,
    BinanceLiveOptions sharedOptions,
    IOrderValidator orderValidator,
    IMarketDataSource source,
    IOrderRouter router,
    ILogger<BinanceLiveConnector> logger)
{ /* assign; _source = source; _router = router; */ }
```

**(b) `LiveSessionEntry`** — replace `LiveOrderContext OrderContext` with `IOrderContext SessionContext` (the facade) + `string AccountName` + a reference to the `IAccountTarget Target`. Keep `EventQueue`, `MarketDataQueue`, `Strategy`, `Subscriptions`, `ExecutionAsset`, `QuoteAsset`, `ProcessingTask`.

**(c) `AddSessionAsync`** — full new body:

```csharp
public async Task AddSessionAsync(LiveSessionConfig config, CancellationToken ct = default)
{
    if (Status != LiveSessionStatus.Running)
        throw new InvalidOperationException($"Connector for account '{AccountName}' is not running.");

    var asset = config.ExecutionAsset;
    var target = await _router.ResolveTarget(config.AccountName, ct);

    // Bind strategy to the per-session facade; route order→session via the account context's event.
    if (target is AccountTarget at)
        at.OrderContext.OrderMapped += (exchangeId, sId) => { _router.TrackOrder(exchangeId, sId); DrainBufferedReports(exchangeId); };
    var sessionContext = target.OrderContextFor(config.SessionId);

    if (config.Strategy is IEventBusReceiver receiver) receiver.SetEventBus(NullEventBus.Instance);
    if (config.Strategy is IOrderContextReceiver orderReceiver) orderReceiver.SetOrderContext(sessionContext);
    config.Strategy.OnInit();

    var entry = new LiveSessionEntry(config.SessionId, config.Strategy, sessionContext, target,
        config.AccountName, config.Subscriptions, asset, asset.QuoteAssetName(/*existing quote lookup*/),
        _sharedOptions.LiveChannelCapacity, _sharedOptions.MarketDataChannelCapacity, _logger);
    _sessions.TryAdd(config.SessionId, entry);

    entry.ProcessingTask = Task.Run(() => DrainSessionQueues(
        entry.EventQueue.Reader, entry.MarketDataQueue.Reader, _logger, entry.SessionId, _cts!.Token));

    var registration = new LiveSessionRegistration(
        config.SessionId, config.Strategy, entry.Subscriptions.ToList(), entry.MarketDataQueue.Writer);
    _source.Register(registration);

    var instrumentScales = InstrumentScaleMap.Build(entry.Subscriptions.ToList());
    await _source.EnsureSources(registration, instrument => instrumentScales[instrument]);

    _logger.LogInformation("Session {SessionId} added to account '{Account}' for {Asset}",
        config.SessionId, config.AccountName, asset.Name);
}
```

> The `QuoteAsset` string previously came from `GetExchangeInfoAsync`. Discovery now lives in the factory; the connector’s snapshot still needs the quote asset for balance display. Lift the quote-asset lookup into the entry construction (reuse the existing `symbolInfo.QuoteAsset` call, or fetch once per session). The executor reconciles this against `GetSessionSnapshotAsync`.

**(d) `OnExecutionReport`** — replace `_binanceOrderToSession.TryGetValue` with `_router.TryResolveSession`, and apply fills to the entry’s `Target.Portfolio` via the account context. Keep the buffering path (`_bufferedReports`) keyed by exchange order id. Replace `entry.OrderContext.*` calls with the account context obtained from `entry.Target` (cast to `AccountTarget` for the internal `AddFill`/`GetPendingOrder`/`RemovePendingOrder`/`IsOrderRestFilled`) — fill `new ScaleContext(entry.ExecutionAsset)` as today, and call `entry.Strategy.OnTrade(...)` for the originating session.

**(e) Reconciliation loop** — iterate `_router.Targets` (per account) instead of `_sessions`; for each target run the 3-phase `ReconcileSession` against that account’s open orders. Where it referenced `entry.OrderContext`, use the account `LiveOrderContext` (`((AccountTarget)target).OrderContext`).

**(f) `RemoveSessionAsync`** — unregister from `_source`, drain the session’s queues, then `await _router.ReleaseTarget(entry.AccountName)`.

**(g) `StopAsync`** — drain all sessions, then `await _router.DisposeAsync()` (disposes all targets: flush + cancel-all), then cancel CTS, await reconcile task, dispose WS/api client.

- [ ] **Step 4: Wire DI** (`LiveHostServiceCollectionExtensions.cs`)

Register `IMarketDataSource` → `BinanceMarketDataSource`, `IAccountTargetFactory` → the Binance factory, `IOrderRouter` → `OrderRouter`. Update `BinanceLiveAccountManager`’s `ConnectorFactory`/ctor to pass the new dependencies through (the manager constructs connectors). Keep the singleton lifetimes consistent with the existing `ITickRouter`/`IStrategyDispatch` registrations.

> The router/source are **per-connector** (per transport), not global singletons — a Binance account = its own transport. Construct them inside `BinanceLiveAccountManager.GetOrCreateAsync` (or its `ConnectorFactory`) alongside each `BinanceLiveConnector`, injecting the per-account `BinanceAccountTargetFactory`. Read `BinanceLiveAccountManager:52-61` for the construction site.

- [ ] **Step 5: Build + run the full Infrastructure + WebApi suites**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/`
Then: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/`
Expected: PASS (E2E guard green through the recomposition).

- [ ] **Step 6: Commit** (controller)

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs \
        src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveAccountManager.cs \
        src/AlgoTradeForge.LiveHost.WebApi/LiveHostServiceCollectionExtensions.cs \
        tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/BinanceLiveConnectorE2ETests.cs
git commit -m "refactor(livehost): recompose BinanceLiveConnector over IOrderRouter + IMarketDataSource"
```

---

## Task 9: Drop absolute `InitialCash` from the command/config/request

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Application/Live/StartLiveSessionCommand.cs`
- Modify: `src/AlgoTradeForge.Domain/Live/LiveSessionConfig.cs`
- Modify: `src/AlgoTradeForge.LiveHost.WebApi/Contracts/StartLiveSessionRequest.cs`
- Modify: `src/AlgoTradeForge.LiveHost.Application/Live/StartLiveSessionCommandHandler.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Application.Tests/Live/StartLiveSessionCommandHandlerTests.cs`

**Interfaces:**
- Produces: `StartLiveSessionCommand`/`LiveSessionConfig`/`StartLiveSessionRequest` without an `InitialCash` field; `StartLiveSessionCommandHandler` no longer scales/sets `InitialCash` (the factory discovers funds).

- [ ] **Step 1: Update the handler test** — remove `InitialCash` assertions/inputs; assert the built `LiveSessionConfig` has no cash field and the handler still resolves asset + scales params + adds the session. (Adjust the existing tests in `StartLiveSessionCommandHandlerTests` that set `InitialCash`.)

- [ ] **Step 2: Run to verify it fails to compile.**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/ --filter "FullyQualifiedName~StartLiveSessionCommandHandler"`
Expected: BUILD FAILURE (tests still reference `InitialCash`).

- [ ] **Step 3: Remove the field** from `StartLiveSessionCommand`, `LiveSessionConfig`, `StartLiveSessionRequest`; in `StartLiveSessionCommandHandler` delete the `initialCashScaled = scale.AmountToTicks(command.InitialCash)` line and the `InitialCash = initialCashScaled` config assignment; remove `InitialCash` from the `LiveEndpoints.StartSession` request mapping.

- [ ] **Step 4: Run Application + WebApi tests.**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/`
Then: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/`
Expected: PASS.

- [ ] **Step 5: Commit** (controller)

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/StartLiveSessionCommand.cs \
        src/AlgoTradeForge.Domain/Live/LiveSessionConfig.cs \
        src/AlgoTradeForge.LiveHost.WebApi/Contracts/StartLiveSessionRequest.cs \
        src/AlgoTradeForge.LiveHost.Application/Live/StartLiveSessionCommandHandler.cs \
        src/AlgoTradeForge.LiveHost.WebApi/Endpoints/LiveEndpoints.cs \
        tests/AlgoTradeForge.LiveHost.Application.Tests/Live/StartLiveSessionCommandHandlerTests.cs
git commit -m "refactor(livehost): discover account funds, drop absolute InitialCash from session start"
```

---

## Task 10: Acceptance suite — two-account routing, co-tenancy, lifecycle, discovery

The headline acceptance. Uses a **fake `IAccountTargetFactory`** (in-memory targets), per-account
**NSubstitute `IExchangeOrderClient`**, and one **shared `IMarketDataSource`** so isolation + shared
data are asserted without a live broker.

**Files:**
- Create: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/MultiAccountRoutingTests.cs`
- Create test doubles in the same file (or a `Live/Fakes/` folder): `FakeAccountTargetFactory`, `InMemoryAccountTarget`.

**Interfaces:**
- Consumes: `IOrderRouter`/`OrderRouter`, `IAccountTarget`, `SessionOrderContext`, `LiveOrderContext`, `IExchangeOrderClient`.

- [ ] **Step 1: Write the acceptance tests**

```csharp
// Test 3 — routing isolation (headline)
[Fact]
public async Task TwoAccounts_OrdersIsolated_PortfoliosIsolated()
{
    var clientA = Substitute.For<IExchangeOrderClient>();
    clientA.PlaceOrderAsync(default!, default, default, default, default, default, default)
        .ReturnsForAnyArgs(new ExchangeOrderResult(1L, []));
    var clientB = Substitute.For<IExchangeOrderClient>();
    clientB.PlaceOrderAsync(default!, default, default, default, default, default, default)
        .ReturnsForAnyArgs(new ExchangeOrderResult(2L, []));

    var factory = new FakeAccountTargetFactory(
        ("A", clientA, 100_000_00L), ("B", clientB, 50_000_00L));
    var router = new OrderRouter(factory, NullLogger<OrderRouter>.Instance);

    var targetA = await router.ResolveTarget("A");
    var targetB = await router.ResolveTarget("B");

    var sessX = Guid.NewGuid();
    var ctxX = targetA.OrderContextFor(sessX);
    ctxX.Submit(new Order { Id = 0, Asset = BtcUsdt, Side = OrderSide.Buy,
        Type = OrderType.Limit, Quantity = 0.001m, LimitPrice = 5000000L });

    await Poll(() => clientA.ReceivedCalls().Any());
    await clientA.Received().PlaceOrderAsync("BTCUSDT", OrderSide.Buy, OrderType.Limit,
        0.001m, Arg.Any<decimal?>(), Arg.Any<decimal?>(), Arg.Any<CancellationToken>());
    Assert.Empty(clientB.ReceivedCalls());                       // account B untouched
    Assert.NotEqual(targetA.Portfolio.InitialCash, targetB.Portfolio.InitialCash);  // isolated ledgers
}

// Test 4 — co-tenancy: two sessions, one account, shared portfolio, OnTrade routed by session
[Fact]
public async Task CoTenant_SharedPortfolio_OnTradeRoutesToOriginatingSession() { /* ... */ }

// Test 7 — reference-counted lifecycle
[Fact]
public async Task Target_DisposedOnly_OnLastSessionRelease() { /* ... */ }

// Test 6 — funds discovered, not configured
[Fact]
public async Task NewAccount_PortfolioSeeded_FromDiscoveredFunds() { /* ... */ }
```

> Fill in tests 4/6/7 fully following the same construction pattern. Test 4: resolve `targetA` twice (one session id each), submit one order per session, raise an execution report via the account `LiveOrderContext`’s fill path, assert both sessions share `targetA.Portfolio` and the order→session map (via `router.TryResolveSession`) maps each exchange id to its submitter. Test 7: `ResolveTarget("A")` twice, `ReleaseTarget("A")` once → `InMemoryAccountTarget.Disposed == false`; twice → `true`. Test 6: `FakeAccountTargetFactory` seeds the portfolio from a per-account configured balance; assert `target.Portfolio.InitialCash` equals it.

- [ ] **Step 2: Implement the fakes**

```csharp
internal sealed class InMemoryAccountTarget : IAccountTarget
{
    private readonly LiveOrderContext _ctx;
    public string AccountName { get; }
    public Portfolio Portfolio { get; }
    public bool Disposed { get; private set; }

    public InMemoryAccountTarget(string name, IExchangeOrderClient client, long initialCash)
    {
        AccountName = name;
        Portfolio = new Portfolio { InitialCash = initialCash };
        Portfolio.Initialize();
        _ctx = new LiveOrderContext(Portfolio, new OrderValidator(), NullLogger.Instance, client);
        _ctx.Start(CancellationToken.None);
    }

    internal LiveOrderContext Context => _ctx;
    public IOrderContext OrderContextFor(Guid sessionId) => new SessionOrderContext(sessionId, _ctx);
    public async ValueTask DisposeAsync() { if (Disposed) return; Disposed = true; await _ctx.StopAsync(); }
}

internal sealed class FakeAccountTargetFactory(params (string Account, IExchangeOrderClient Client, long Cash)[] accounts)
    : IAccountTargetFactory
{
    public Task<IAccountTarget> Create(string account, CancellationToken ct = default)
    {
        var (_, client, cash) = accounts.First(a => a.Account == account);
        return Task.FromResult<IAccountTarget>(new InMemoryAccountTarget(account, client, cash));
    }
}

private static async Task Poll(Func<bool> cond)
{
    for (int i = 0; i < 100 && !cond(); i++) await Task.Delay(10);
}
```

- [ ] **Step 3: Run the acceptance suite**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "FullyQualifiedName~MultiAccountRoutingTests"`
Expected: PASS (all four acceptance tests).

- [ ] **Step 4: Full regression** — run every LiveHost suite sequentially.

Run: `dotnet build AlgoTradeForge.slnx`
Then: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/`
Then: `dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/`
Then: `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/`
Expected: ALL PASS.

- [ ] **Step 5: Commit** (controller)

```bash
git add tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/MultiAccountRoutingTests.cs
git commit -m "test(livehost): two-account routing isolation, co-tenancy, lifecycle, funds discovery"
```

---

## Verification (maps to spec acceptance)

- Spine acceptance #1 (two-account routing isolation) → Task 10 test 3.
- Spine acceptance #2 (per-target scaling correctness) → Task 1 test + Task 2 (data-plane).
- Shared-Portfolio multi-writer / co-tenancy → Task 10 test 4 (lock guard verified in `LiveOrderContext.AddFill`).
- Race-safe get-or-create → Task 5 `ResolveTarget` test.
- Reference-counted teardown + idempotent dispose → Task 4 + Task 5 + Task 10 test 7.
- Funds discovered not configured → Task 7 + Task 9 + Task 10 test 6.
- Binance degenerate regression → Task 8 (existing E2E green through recomposition).
- Both single-scale constants removed → Task 1 (`:225`) + Task 8 (`:325`).
