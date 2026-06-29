# LiveHost Plan 4 — IB Order Session + Single-Socket Cohabit — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Plug IB order execution into the Plan 2 `IOrderRouter`/`IAccountTarget` seam, sharing the Plan 3 `IbSession` socket, so one IB login both collects ticks and executes paper orders in one process.

**Architecture:** First **extract** the venue-neutral per-session dispatch core (`ExecutionReport` + `LiveSessionDispatcher`) out of `BinanceLiveConnector` so both venues compose it. Then **grow the IB order seam** on the shared socket (connection-scoped order-id allocator, order/fill/status callbacks in `IbWrapper`, an `IbOrderGateway` that places/cancels and maps IB callbacks → neutral `ExecutionReport` off the pump thread). Then add **per-account adapters** (`IbAccountFundsSource`, neutral `AccountTargetFactory`, `IbExchangeOrderClient`, `IbMarketDataSource`) and an **`IbLiveConnector`** composition root. Finally wire **reconnect reconciliation** against IB's account-wide open-order pushback, diffed against the **union** of every co-tenant session's expected orders.

**Tech Stack:** C# 14 / .NET 10, xUnit + NSubstitute, vendored IBApi 10.45.01 (`src/AlgoTradeForge.IbApi`), `System.Threading.Channels`.

## Global Constraints

- **Branch off `main` AFTER Plan 2 squash-merges** (Plan 4 depends on Plans 2 AND 3). The extraction refactors Plan 2's merged code. Do NOT execute on the Plan 2 branch.
- **One `dotnet` process at a time.** Build `dotnet build AlgoTradeForge.slnx`; test `dotnet test tests/<Project.Tests>/` sequentially. `powershell.exe`, never `pwsh`.
- **Subagents must NOT run `git` mutating commands** (no `reset`/`checkout`/`stash`/`commit`/`add`). Leave changes in the working tree; the CONTROLLER commits per task after review.
- **Test-First:** failing xUnit test before implementation. `TestContext.Current.CancellationToken` on every awaited test call (xUnit1051 is an error). NSubstitute on internal seams via the existing `InternalsVisibleTo("DynamicProxyGenAssembly2")`.
- **Conventions:** one type per file; no `Async` suffix on new async methods; `CancellationToken ct = default` on async APIs; no sync-over-async (`.Result`/`.Wait()`/`.GetAwaiter().GetResult()`); `using`-over-try/finally; no `catch when (ex is not OperationCanceledException)` in long-running loops — use `IsTrueShutdown(ex, ct)`; terse comments only.
- **Int64 money:** `MoneyConvert.ToLong` in Domain; `ScaleContext` at boundaries; the IB connector does independent price/qty scaling at its own boundary.
- **Domain stays venue-neutral, ZERO new ProjectReferences.** All IB types `internal`; the `IBApi` reference is confined to the `InteractiveBrokers/` slice; IB internals exposed only via the `AddIbDataPlane`/`AddIbOrderPlane` DI extension (+ `VenueKind`/`VenueSelector`, already public).
- **Every channel bounded.** The order/execution path stays independent of market data (off the EReader pump thread).
- **Reference design:** `docs/superpowers/specs/2026-06-28-livehost-plan4-ib-order-session-design.md`.

---

## File Structure

**Phase A — extraction (venue-neutral; refactors Plan 2 code):**
- Create `src/AlgoTradeForge.LiveHost.Application/Live/ExecType.cs` — neutral execution-report kind enum.
- Create `src/AlgoTradeForge.LiveHost.Application/Live/ExecutionReport.cs` — neutral inbound report record.
- Create `src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveSessionEntry.cs` — lifted from `BinanceLiveConnector` (per-session state).
- Create `src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveSessionDispatcher.cs` — lifted session table + queues + report routing + reconciliation, parameterized by `IOrderRouter`/`IMarketDataSource`.
- Create `src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveDispatcherOptions.cs` — capacities + reconciliation interval (record).
- Modify `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs` — shrink to transport + `BinanceExecutionReport → ExecutionReport` mapping; compose the dispatcher.
- Test `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/LiveSessionDispatcherTests.cs`.

**Phase B — IB order seam (all `internal`, `Infrastructure/Live/InteractiveBrokers/`):**
- Modify `IbConnection.cs` — add `NextOrderId()` seeded from `NextValidId`.
- Create `IIbOrderClient.cs` — internal order request seam (`placeOrder`/`cancelOrder`/`NextOrderId`).
- Create `IbOrderUpdate.cs` — raw IB order/fill DTOs (`IbOrderAck`, `IbFill`, `IbOpenOrder`).
- Modify `IbWrapper.cs` — order/fill/status correlators + ack awaiters + `execId` dedup + pushback accumulator.
- Create `IbOrderGateway.cs` — place/cancel, ack awaiters, IB cb → `ExecutionReport`, off-pump order-event lane, `Reconnected` → reconcile signal.
- Test `tests/.../Live/InteractiveBrokers/IbConnectionOrderIdTests.cs`, `IbWrapperOrderTests.cs`, `IbOrderGatewayTests.cs`, plus `FakeIbOrderClient.cs`.

**Phase C — per-account adapters:**
- Create `IbExchangeOrderClient.cs` (`: IExchangeOrderClient`) — per-account, tags `Order.Account`, maps `OrderType`.
- Create `IbAccountFundsSource.cs` (`: IAccountFundsSource`) — `reqAccountSummary` funds.
- Rename `Binance/BinanceAccountTargetFactory.cs` → `Live/AccountTargetFactory.cs` (neutral), generalize the order-client dependency to a per-account provider.
- Rename `Binance/BinanceMarketDataSource.cs` → `Live/DispatchMarketDataSource.cs` (neutral; already venue-agnostic) and reuse it for both venues — no IB-specific copy.
- Tests for each.

**Phase D — wiring:**
- Create `IbLiveConnector.cs` — composition root sharing `IbSession` (data + order).
- Modify `IbDataPlaneServiceCollectionExtensions.cs` (or a sibling `IbOrderPlaneServiceCollectionExtensions.cs`) — DI for the order plane.
- Test `IbLiveConnectorTests.cs`.

**Phase E — reconciliation + gated paper:**
- Modify `LiveSessionDispatcher.cs` — per-target **union** reconcile + `ReconcileFromSnapshot(account, brokerOpenOrderIds)`.
- Modify `IbOrderGateway.cs` — feed the pushback snapshot on `Reconnected`.
- Test `LiveSessionDispatcherUnionReconcileTests.cs`; gated `IbOrderPlanePaperTests.cs` (`[Trait("Category","IbPaper")]`).

---

## PHASE A — Extract the venue-neutral dispatch core

### Task A1: Neutral `ExecType` + `ExecutionReport`

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/ExecType.cs`
- Create: `src/AlgoTradeForge.LiveHost.Application/Live/ExecutionReport.cs`
- Test: folded into Task A2 (a bare record has no behavior to test independently; it is exercised by the dispatcher tests).

**Interfaces:**
- Produces: `enum ExecType { New, Trade, Canceled, Expired, Rejected }`; `sealed record ExecutionReport(long OrderId, Asset Asset, OrderSide Side, ExecType ExecType, decimal LastFillPrice, decimal LastFillQty, decimal Commission, OrderStatus Status)`. Prices/quantities are in **market units** (the dispatcher scales via `new ScaleContext(report.Asset)`), matching today's `HandleTradeExecution`.

- [ ] **Step 1: Create `ExecType.cs`**

```csharp
namespace AlgoTradeForge.LiveHost.Application.Live;

// Venue-neutral execution-report kind. Venue connectors map their raw report type (e.g.
// BinanceExecutionReport.ExecutionType string, IB orderStatus/execDetails) onto this.
public enum ExecType
{
    New,
    Trade,
    Canceled,
    Expired,
    Rejected,
}
```

- [ ] **Step 2: Create `ExecutionReport.cs`**

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.LiveHost.Application.Live;

// Venue-neutral inbound execution report fed to LiveSessionDispatcher.OnExecutionReport.
// Prices/quantities are in market units; the dispatcher scales via new ScaleContext(Asset).
public sealed record ExecutionReport(
    long OrderId,
    Asset Asset,
    OrderSide Side,
    ExecType ExecType,
    decimal LastFillPrice,
    decimal LastFillQty,
    decimal Commission,
    OrderStatus Status);
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build src/AlgoTradeForge.LiveHost.Application/`
Expected: build succeeds, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Application/Live/ExecType.cs src/AlgoTradeForge.LiveHost.Application/Live/ExecutionReport.cs
git commit -m "feat(livehost): add neutral ExecutionReport + ExecType for the dispatch core"
```

---

### Task A2: Extract `LiveSessionEntry` + `LiveSessionDispatcher` from `BinanceLiveConnector`

This is the load-bearing refactor. It **moves** existing, tested behavior into a venue-neutral component; the guard is the dispatcher's own unit tests plus the already-green `LiveOrderContextTests` (15), `OrderRouterTests` (5), `MultiAccountRoutingTests` (6). Behavior is preserved exactly; only the report type at the boundary becomes neutral and the venue-specific tails (quote-asset resolution, transport teardown) stay in the connector.

**Files:**
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveSessionEntry.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveDispatcherOptions.cs`
- Create: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/LiveSessionDispatcher.cs`
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/LiveSessionDispatcherTests.cs`

**Interfaces:**
- Consumes: `IOrderRouter` (`ResolveTarget(account, Asset, ct)`, `ReleaseTarget`, `TrackOrder`, `UntrackOrder`, `TryResolveSession`, `Targets`), `IMarketDataSource` (`Register`/`EnsureSources`/`RecentBars`/`RemoveSources`), `OrderGroupReconciler` (`DetectAsync`/`CancelOrphansAsync`), `AccountTarget`/`LiveOrderContext`, `LiveSessionConfig`, `LiveSessionRegistration`, `InstrumentScaleMap.Build`, `CoTenancyRule.Conflict`, `ITradeRegistryProvider`, `ExecutionReport`.
- Produces:

```csharp
// LiveSessionDispatcher.cs (public sealed class)
public LiveSessionDispatcher(IOrderRouter router, IMarketDataSource source,
    OrderGroupReconciler reconciler, LiveDispatcherOptions options, ILogger logger);

// quoteAsset is resolved by the venue connector (Binance: GetExchangeInfoAsync; IB: funds/contract currency)
Task AddSession(LiveSessionConfig config, string quoteAsset, CancellationToken ct = default);
Task RemoveSession(Guid sessionId, CancellationToken ct = default);
void OnExecutionReport(ExecutionReport report);   // routing + AddFill + OnTrade; buffers unmapped
void StartReconciliation(CancellationToken ct);   // periodic per-target union reconcile loop
Task Stop(CancellationToken ct = default);        // drain sessions → dispose router (cancels-all)
IReadOnlyCollection<Guid> SessionIds { get; }     // for the connector's safety-net cancel-all
internal bool IsTrueShutdown(Exception ex, CancellationToken ct);
```

- [ ] **Step 1: Write the failing dispatcher test**

```csharp
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Infrastructure.Live;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live;

public sealed class LiveSessionDispatcherTests
{
    [Fact]
    public async Task OnExecutionReport_RoutesFillToOriginatingSession_AndAppliesToSharedPortfolio()
    {
        var ct = TestContext.Current.CancellationToken;
        var fixture = await DispatcherFixture.WithOneSession(ct);

        fixture.Dispatcher.OnExecutionReport(new ExecutionReport(
            OrderId: fixture.ExchangeOrderId,
            Asset: fixture.Asset,
            Side: OrderSide.Buy,
            ExecType: ExecType.Trade,
            LastFillPrice: 100m,
            LastFillQty: 1m,
            Commission: 0m,
            Status: OrderStatus.Filled));

        await fixture.DrainEventQueue(ct);

        Assert.Single(fixture.Strategy.ReceivedTrades);
        Assert.Equal(1m, fixture.Target.Portfolio.Positions[fixture.Asset.Name].Quantity);
    }

    [Fact]
    public void OnExecutionReport_BuffersUnmappedOrder_ReplaysOnTrack()
    {
        var fixture = DispatcherFixture.WithRouterOnly();
        var report = new ExecutionReport(999, fixture.Asset, OrderSide.Buy, ExecType.Trade,
            100m, 1m, 0m, OrderStatus.Filled);

        fixture.Dispatcher.OnExecutionReport(report); // unmapped → buffered, no throw
        // mapping arrives later:
        fixture.MapOrder(999, fixture.SessionId);

        Assert.Contains(999L, fixture.ReplayedOrderIds);
    }
}
```

(`DispatcherFixture` is a test helper built in this same file: it wires an in-memory `IAccountTargetFactory` returning a real `AccountTarget` over an NSubstitute `IExchangeOrderClient`, a real `OrderRouter`, a fake `IMarketDataSource`, and a stub `IInt64BarStrategy` recording `OnTrade`. Mirror `MultiAccountRoutingTests` for the fakes.)

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter LiveSessionDispatcherTests`
Expected: FAIL — `LiveSessionDispatcher` does not exist.

- [ ] **Step 3: Create `LiveDispatcherOptions.cs`**

```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Capacities + cadence for the per-session dispatch core (lifted from BinanceLiveOptions usage).
public sealed record LiveDispatcherOptions(
    int EventQueueCapacity,
    int MarketDataQueueCapacity,
    TimeSpan ReconciliationInterval);
```

- [ ] **Step 4: Create `LiveSessionEntry.cs` (lift verbatim from `BinanceLiveConnector`)**

Move the existing `private sealed class LiveSessionEntry` (BinanceLiveConnector.cs:76-143) to its own file as `internal sealed class LiveSessionEntry`. No body changes — same fields, same `EventQueue`/`MarketDataQueue` construction, same `OrderContext => Target.OrderContext`.

- [ ] **Step 5: Create `LiveSessionDispatcher.cs`**

Move these members from `BinanceLiveConnector` **unchanged in behavior**, re-parameterized on the injected `IOrderRouter _router`, `IMarketDataSource _source`, `OrderGroupReconciler _reconciler`, `LiveDispatcherOptions _options`, `ILogger _logger`, plus `_cts` owned by the dispatcher (created in a `Start(CancellationToken)` the connector calls after building seams):
- Fields: `_sessions`, `_removedSessions`, `_bufferedReports`.
- `DrainSessionQueues`, `RunCallback`, `IsTrueShutdown` (move the `internal static` helpers).
- `AddSession` ← `AddSessionAsync` body **minus** the venue-specific head (`GetExchangeInfoAsync`): take `quoteAsset` as a parameter; everything from `ResolveTarget` onward is unchanged (co-tenancy fence, `OrderMapped` handler, `LiveSessionEntry` creation, `DrainSessionQueues` task, `Register`/`EnsureSources` with `InstrumentScaleMap.Build`).
- `RemoveSession` ← `RemoveSessionAsync` (unchanged).
- `StartReconciliation`/the reconcile loop/`LogReconciliationFailure` ← `RunReconciliationLoop` (unchanged for now; Task E1 generalizes `ReconcileSession` to the union).
- `OnExecutionReport(ExecutionReport)` ← rewrite the Binance `OnExecutionReport(BinanceExecutionReport)` switch to dispatch on `report.ExecType` (`Trade → HandleTrade`, `Canceled/Expired → HandleTermination(Cancelled)`, `Rejected → HandleTermination(Rejected)`); `DrainBufferedReports` unchanged.
- `HandleTrade(ExecutionReport, LiveSessionEntry)` ← `HandleTradeExecution` with the **string parsing removed**: use `report.LastFillPrice`/`LastFillQty`/`Commission` directly via `var scale = new ScaleContext(report.Asset); var fillPrice = scale.FromMarketPrice(report.LastFillPrice);` etc.; the rest (TryWrite onto `EventQueue`, `AddFill`, pending-order status from `report.Status`, `entry.Strategy.OnTrade`) is unchanged. The `IsOrderRestFilled` skip stays (always false for IB).
- `HandleTermination(ExecutionReport, LiveSessionEntry, OrderStatus)` ← `HandleOrderTermination` (unchanged).
- `Stop(CancellationToken)` ← `StopAsync` **steps 1-4 + 6** (drain sessions → dispose router → cancel CTS → await reconcile → clear sessions). The venue-specific **step 5 safety-net cancel-all** and WS/transport teardown stay in the connector, which calls `dispatcher.Stop()` then tears down its own transport.

Provide the public surface from the Interfaces block above.

- [ ] **Step 6: Modify `BinanceLiveConnector` to compose the dispatcher**

In `ConnectAsync`, after building `_source`/`_fundsSource`/`_factory`/`_router`/`_reconciler`, construct `_dispatcher = new LiveSessionDispatcher(_router, _source, _reconciler, new LiveDispatcherOptions(_sharedOptions.LiveChannelCapacity, _sharedOptions.MarketDataChannelCapacity, _sharedOptions.ReconciliationInterval), _logger);` and call `_dispatcher.Start(_cts.Token)` + `_dispatcher.StartReconciliation(_cts.Token)`. `OnExecutionReport` becomes a thin mapper:

```csharp
private void OnExecutionReport(BinanceExecutionReport report)
{
    var entry = _dispatcher.TryGetExecutionAsset(report.OrderId, out var asset, out var ok);
    // resolve the asset for this order (router/target) — see note below
    _dispatcher.OnExecutionReport(MapToNeutral(report, asset));
}
```

Asset note: today `HandleTradeExecution` reads `entry.ExecutionAsset`. The neutral `ExecutionReport` carries `Asset`, so the **connector must supply it**. Resolve it via the session: `_router.TryResolveSession(report.OrderId, out var sid)` → `_sessions`/dispatcher lookup. Simplest faithful approach: expose `bool TryResolveAsset(long orderId, out Asset asset)` on the dispatcher (looks up session → `entry.ExecutionAsset`); if unmapped, the connector still maps with the account's seed asset (the dispatcher will buffer by order id regardless). `MapToNeutral` parses the Binance strings (the parsing currently in `HandleTradeExecution`) and sets `ExecType`/`Status` from the Binance `ExecutionType`/`OrderStatus` strings.

`AddSessionAsync` keeps only: `GetExchangeInfoAsync(asset.Name)` → `quoteAsset`, then `await _dispatcher.AddSession(config, quoteAsset, ct)`. `RemoveSessionAsync` → `await _dispatcher.RemoveSession(sessionId, ct)`. `StopAsync` → `await _dispatcher.Stop(ct)` then the safety-net cancel-all over `_dispatcher.SessionIds`'-assets + `_wsManager`/`_apiClient` teardown.

- [ ] **Step 7: Run the dispatcher tests + the Plan 2 regression suites**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "LiveSessionDispatcherTests|LiveOrderContextTests|OrderRouterTests|MultiAccountRoutingTests|CoTenancyRuleTests|AccountTargetTests"`
Expected: PASS (dispatcher tests green; the 15+5+6 Plan 2 tests unchanged).

- [ ] **Step 8: Full Infrastructure + WebApi build/test**

Run: `dotnet build AlgoTradeForge.slnx` then `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/` then `dotnet test tests/AlgoTradeForge.LiveHost.WebApi.Tests/`
Expected: build 0/0; suites green (testnet-gated `BinanceLiveConnectorE2ETests` skip).

- [ ] **Step 9: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/ tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/LiveSessionDispatcherTests.cs
git commit -m "refactor(livehost): extract venue-neutral LiveSessionDispatcher from BinanceLiveConnector"
```

---

## PHASE B — IB order seam on the shared socket

### Task B1: `IbConnection.NextOrderId()` seeded from `nextValidId`

**Files:**
- Modify: `src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbConnection.cs`
- Test: `tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbConnectionOrderIdTests.cs`

**Interfaces:**
- Consumes: `IbWrapper.NextValidId` (`Task<int>`), set on `nextValidId(int)`.
- Produces: `int IbConnection.NextOrderId()` — monotonic, seeded from the awaited `NextValidId` at connect, re-armed on reconnect.

- [ ] **Step 1: Write the failing test**

```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public sealed class IbConnectionOrderIdTests
{
    [Fact]
    public void NextOrderId_StartsFromSeed_AndIncrementsMonotonically()
    {
        var conn = IbConnectionTestFactory.WithSeededNextValidId(5000);
        Assert.Equal(5000, conn.NextOrderId());
        Assert.Equal(5001, conn.NextOrderId());
        Assert.Equal(5002, conn.NextOrderId());
    }

    [Fact]
    public void SeedNextOrderId_ReArmsToLargerServerValue_OnReconnect()
    {
        var conn = IbConnectionTestFactory.WithSeededNextValidId(5000);
        conn.NextOrderId(); // 5000
        conn.SeedNextOrderId(9000); // reconnect hands back a higher seed
        Assert.Equal(9000, conn.NextOrderId());
    }
}
```

(`IbConnectionTestFactory` exposes a way to set the seed without a real socket — extract the counter into an internal `SeedNextOrderId(int)` + `NextOrderId()` so the test drives them directly. If `IbConnection` cannot be constructed without a socket in tests, move the order-id counter to a tiny internal `OrderIdAllocator` class and test that; `IbConnection` delegates to it.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter IbConnectionOrderIdTests`
Expected: FAIL — `NextOrderId`/`SeedNextOrderId` not defined.

- [ ] **Step 3: Implement on `IbConnection`**

```csharp
private int _nextOrderId;      // order-id space, distinct from _nextReqId; seeded from nextValidId
private int _orderIdSeeded;    // 0 until first seed

// Called by Connect after `await wrapper.NextValidId` (and on reconnect re-arm).
public void SeedNextOrderId(int seed)
{
    // Re-arm to the server's value; never go backwards (a stale reconnect seed must not rewind).
    var current = Volatile.Read(ref _nextOrderId);
    if (Interlocked.Exchange(ref _orderIdSeeded, 1) == 0 || seed > current)
        Volatile.Write(ref _nextOrderId, seed);
}

// One id per order (brackets are individual strategy-side orders — no consecutive reservation).
public int NextOrderId() => Interlocked.Increment(ref _nextOrderId) - 1; // returns seed, then seed+1, …
```

In `Connect`, after `await wrapper.NextValidId.WaitAsync(...)` (line 57), capture and seed: `SeedNextOrderId(await wrapper.NextValidId);`. (`NextValidId` is already complete at that point.)

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter IbConnectionOrderIdTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbConnection.cs tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/Live/InteractiveBrokers/IbConnectionOrderIdTests.cs
git commit -m "feat(livehost): add IbConnection.NextOrderId seeded from nextValidId"
```

---

### Task B2: IB order DTOs + `IIbOrderClient` seam

**Files:**
- Create: `src/.../InteractiveBrokers/IbOrderUpdate.cs` (raw DTOs)
- Create: `src/.../InteractiveBrokers/IIbOrderClient.cs`
- Test: folded into B3/B4 (these are data + a mockable seam).

**Interfaces:**
- Produces:

```csharp
// IbOrderUpdate.cs
internal readonly record struct IbOrderStatusUpdate(int OrderId, string Status, decimal Filled, decimal Remaining, double AvgFillPrice);
internal readonly record struct IbFill(int OrderId, string ExecId, double Price, decimal Qty, string Side, long TimeUnixSec);
internal readonly record struct IbOpenOrder(int OrderId, string Account, string Symbol, string Side, string OrderType, decimal Quantity, double LmtPrice, double AuxPrice, string Status);

// IIbOrderClient.cs — order request surface over the shared socket (mockable; mirrors IIbMarketDataClient)
internal interface IIbOrderClient
{
    int NextOrderId();
    void PlaceOrder(int orderId, ResolvedIbContract contract, IbOrderRequest request);
    void CancelOrder(int orderId);
}

// IbOrderRequest.cs (beside IIbOrderClient): venue-neutral-to-IB order intent the client translates to IBApi.Order
internal readonly record struct IbOrderRequest(string Account, string Action, string OrderType, decimal Quantity, double? LmtPrice, double? AuxPrice);
```

- [ ] **Step 1: Create the DTO + seam files** with the exact content above (one type per file; `IbOrderRequest` may share `IIbOrderClient.cs` as a single-line record per the file-org exception).

- [ ] **Step 2: Build**

Run: `dotnet build src/AlgoTradeForge.LiveHost.Infrastructure/`
Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IbOrderUpdate.cs src/AlgoTradeForge.LiveHost.Infrastructure/Live/InteractiveBrokers/IIbOrderClient.cs
git commit -m "feat(livehost): add IB order DTOs + IIbOrderClient seam"
```

---

### Task B3: `IbWrapper` order/fill/status callbacks + `execId` dedup + ack awaiters + pushback accumulator

**Files:**
- Modify: `src/.../InteractiveBrokers/IbWrapper.cs`
- Test: `tests/.../Live/InteractiveBrokers/IbWrapperOrderTests.cs`

**Interfaces:**
- Produces, added to `IbWrapper`:

```csharp
// Order-event sink installed by IbOrderGateway (keyed by orderId for status/ack; fills carry orderId).
public void RegisterOrderSink(Action<IbOrderStatusUpdate> onStatus, Action<IbFill> onFill);
// Ack awaiter: completes on first orderStatus/openOrder for orderId; faults on a reject-coded error.
public Task<IbOrderStatusUpdate> RegisterOrderAck(int orderId);
public void ReleaseOrderAck(int orderId);
// Reconnect pushback: openOrder accumulates, openOrderEnd completes the snapshot.
public Task<IReadOnlyList<IbOpenOrder>> BeginOpenOrderSnapshot();
public event Action<IbFill>? Fill;            // optional convenience; primary is the registered sink
```

- IB callbacks overridden: `orderStatus`, `openOrder`, `openOrderEnd`, `execDetails`, `commissionAndFeesReport`. `execDetails` dedups by `execId` (bounded seen-set, capacity e.g. 4096 LRU). `error` gains a **reject-code filter**.

- [ ] **Step 1: Write the failing tests**

```csharp
namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public sealed class IbWrapperOrderTests
{
    [Fact]
    public async Task OrderStatus_CompletesAck()
    {
        var w = new IbWrapper();
        var ack = w.RegisterOrderAck(42);
        w.orderStatus(42, "Submitted", 0, 1, 0, 0, 0, 0, 0, "", 0);
        var result = await ack.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal("Submitted", result.Status);
    }

    [Fact]
    public void ExecDetails_DedupsByExecId()
    {
        var w = new IbWrapper();
        var fills = new List<IbFill>();
        w.RegisterOrderSink(_ => { }, fills.Add);
        var exec = IbExecFactory.Make(orderId: 42, execId: "E1", shares: 1, price: 100);
        w.execDetails(1, IbExecFactory.Contract(), exec);
        w.execDetails(1, IbExecFactory.Contract(), exec); // replay (reconnect)
        Assert.Single(fills); // applied once
    }

    [Fact]
    public async Task Error_WithWarningCode_DoesNotFaultAck()
    {
        var w = new IbWrapper();
        var ack = w.RegisterOrderAck(42);
        w.error(42, 0, 399, "order message warning", ""); // 399 = informational
        Assert.False(ack.IsFaulted);
        w.orderStatus(42, "Submitted", 0, 1, 0, 0, 0, 0, 0, "", 0);
        var result = await ack.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal("Submitted", result.Status);
    }

    [Fact]
    public void Error_WithRejectCode_FaultsAck()
    {
        var w = new IbWrapper();
        var ack = w.RegisterOrderAck(42);
        w.error(42, 0, 201, "order rejected", ""); // 201 = rejected
        Assert.True(ack.IsFaulted);
    }
}
```

(`IbExecFactory` builds `IBApi.Execution`/`Contract`; `orderStatus` signature is the 10.45 11-arg form — verify against the vendored `EWrapper`.)

- [ ] **Step 2: Run to verify failure** — Run: `dotnet test ... --filter IbWrapperOrderTests` → FAIL (members not defined).

- [ ] **Step 3: Implement in `IbWrapper`** — add the fields + overrides:

```csharp
private Action<IbOrderStatusUpdate>? _onStatus;
private Action<IbFill>? _onFill;
private readonly ConcurrentDictionary<int, TaskCompletionSource<IbOrderStatusUpdate>> _acks = new();
private readonly HashSet<string> _seenExecIds = new(); // bounded; evict oldest past capacity
private readonly Queue<string> _execIdOrder = new();
private List<IbOpenOrder>? _openOrders;
private TaskCompletionSource<IReadOnlyList<IbOpenOrder>>? _openOrderSnapshot;

// Reject codes that fault an ack (placement/risk rejects). Warnings (399, 2100-2199, 10167, …) pass through.
private static readonly HashSet<int> RejectCodes = [201, 202, 10052, /* risk/precautionary rejects added during impl */ ];

public void RegisterOrderSink(Action<IbOrderStatusUpdate> onStatus, Action<IbFill> onFill)
{ _onStatus = onStatus; _onFill = onFill; }

public Task<IbOrderStatusUpdate> RegisterOrderAck(int orderId)
{ var tcs = new TaskCompletionSource<IbOrderStatusUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
  _acks[orderId] = tcs; return tcs.Task; }

public void ReleaseOrderAck(int orderId) => _acks.TryRemove(orderId, out _);

public Task<IReadOnlyList<IbOpenOrder>> BeginOpenOrderSnapshot()
{ _openOrders = []; _openOrderSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
  return _openOrderSnapshot.Task; }

public override void orderStatus(int orderId, string status, decimal filled, decimal remaining,
    double avgFillPrice, long permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
{
    var update = new IbOrderStatusUpdate(orderId, status, filled, remaining, avgFillPrice);
    if (_acks.TryGetValue(orderId, out var tcs)) tcs.TrySetResult(update);
    _onStatus?.Invoke(update);
}

public override void execDetails(int reqId, Contract contract, Execution execution)
{
    if (!MarkExecSeen(execution.ExecId)) return; // dedup
    _onFill?.Invoke(new IbFill(execution.OrderId, execution.ExecId, execution.Price,
        execution.Shares, execution.Side, execution.Time is null ? 0 : ParseIbTime(execution.Time)));
}

public override void commissionAndFeesReport(CommissionAndFeesReport report) { /* deferred cash-adjust (open point #2): log */ }

public override void openOrder(int orderId, Contract contract, Order order, OrderState state)
    => _openOrders?.Add(new IbOpenOrder(orderId, order.Account ?? "", contract.Symbol, order.Action,
        order.OrderType, order.TotalQuantity, order.LmtPrice, order.AuxPrice, state.Status));

public override void openOrderEnd()
{ var snap = _openOrders ?? []; _openOrderSnapshot?.TrySetResult(snap); _openOrders = null; }

private bool MarkExecSeen(string execId)
{
    lock (_execIdOrder)
    {
        if (!_seenExecIds.Add(execId)) return false;
        _execIdOrder.Enqueue(execId);
        if (_execIdOrder.Count > 4096) _seenExecIds.Remove(_execIdOrder.Dequeue());
        return true;
    }
}
```

Extend the existing `error` override: before the `id >= 0` market-data-fault branch, add: `if (_acks.TryGetValue(id, out var ackTcs) && RejectCodes.Contains(errorCode)) { ackTcs.TrySetException(new IbRequestException(errorCode, errorMsg)); return; }` — and for non-reject codes on a known ack id, log + return (do not fault). Keep the existing `id == -1` connectivity handling and the `_byReq`/`_histByReq` fault paths.

- [ ] **Step 4: Run to verify pass** — Run: `dotnet test ... --filter IbWrapperOrderTests` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/.../InteractiveBrokers/IbWrapper.cs tests/.../InteractiveBrokers/IbWrapperOrderTests.cs
git commit -m "feat(livehost): IbWrapper order/fill/status callbacks, execId dedup, reject-code ack filter"
```

---

### Task B4: `IbOrderGateway` — place/cancel, ack awaiter, off-pump order-event lane

**Files:**
- Create: `src/.../InteractiveBrokers/IbOrderGateway.cs`
- Test: `tests/.../InteractiveBrokers/IbOrderGatewayTests.cs`, `FakeIbOrderClient.cs`

**Interfaces:**
- Consumes: `IIbOrderClient` (B2), `IbWrapper` order sink/ack (B3), `IbSession.Reconnected` (Plan 3), an `Action<ExecutionReport>` dispatch callback supplied by the connector, and an `Asset`/contract resolver to tag fills with their Domain `Asset`.
- Produces:

```csharp
internal sealed class IbOrderGateway : IAsyncDisposable
{
    public IbOrderGateway(IIbOrderClient client, IbWrapper wrapper, Action<ExecutionReport> onReport,
        Func<int, (Asset Asset, OrderSide Side)?> orderInfo, ILogger logger, int laneCapacity = 4096);

    // Allocates id, places, awaits first ack (bounded timeout); throws IbRequestException on reject/timeout.
    public Task<long> Place(string account, ResolvedIbContract contract, IbOrderRequest request, CancellationToken ct = default);
    public void Cancel(long orderId);
    public ValueTask DisposeAsync();
}
```

- The gateway tracks `orderId → (Asset, Side)` (set in `Place`, read by the fill mapper) so `execDetails` → `ExecutionReport(asset, side, …)`. It owns the bounded order-event lane: the `IbWrapper` fill/status sink (pump thread) does `TryWrite`; a single worker drains → builds `ExecutionReport` → `onReport`.

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class IbOrderGatewayTests
{
    [Fact]
    public async Task Place_AllocatesId_Places_AwaitsAck_ReturnsId()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        var gw = GatewayFixture.Build(client, wrapper, reports.Add);

        var placeTask = gw.Place("DU1", GatewayFixture.Aapl, GatewayFixture.MktBuy(1), ct);
        client.SignalAck(wrapper, "Submitted"); // fake fires orderStatus on the wrapper
        var id = await placeTask;

        Assert.Equal(100, id);
        Assert.Equal(100, client.LastPlacedOrderId);
    }

    [Fact]
    public async Task Place_OnRejectError_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        var gw = GatewayFixture.Build(client, wrapper, _ => { });
        var placeTask = gw.Place("DU1", GatewayFixture.Aapl, GatewayFixture.MktBuy(1), ct);
        wrapper.error(100, 0, 201, "rejected", "");
        await Assert.ThrowsAsync<IbRequestException>(() => placeTask);
    }

    [Fact]
    public async Task ExecDetails_EmitsExecutionReport_OffPump_WithAssetAndSide()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = new FakeIbOrderClient(seedId: 100);
        var wrapper = new IbWrapper();
        var reports = new List<ExecutionReport>();
        var gw = GatewayFixture.Build(client, wrapper, reports.Add);
        var placeTask = gw.Place("DU1", GatewayFixture.Aapl, GatewayFixture.MktBuy(1), ct);
        client.SignalAck(wrapper, "Submitted");
        await placeTask;

        wrapper.execDetails(1, GatewayFixture.AaplContract, IbExecFactory.Make(100, "E1", 1, 100));
        await GatewayFixture.WaitForReport(reports, ct);

        Assert.Single(reports);
        Assert.Equal(ExecType.Trade, reports[0].ExecType);
        Assert.Equal(OrderSide.Buy, reports[0].Side);
        Assert.Equal(0m, reports[0].Commission); // gross at emit
    }
}
```

- [ ] **Step 2: Run to verify failure** → FAIL.

- [ ] **Step 3: Implement `IbOrderGateway`** with: a `ConcurrentDictionary<int,(Asset,OrderSide)> _orderInfo`; `Place` does `var id = _client.NextOrderId(); _orderInfo[id] = (...); var ack = _wrapper.RegisterOrderAck(id); _client.PlaceOrder(id, contract, request); try { await ack.WaitAsync(_ackTimeout, ct); } finally { _wrapper.ReleaseOrderAck(id); } return id;`. Register the fill/status sink once in the ctor: `wrapper.RegisterOrderSink(onStatus: _ => {}, onFill: f => _lane.Writer.TryWrite(f));`. The worker drains `_lane`, looks up `_orderInfo[f.OrderId]`, builds `new ExecutionReport(f.OrderId, asset, side, ExecType.Trade, (decimal)f.Price, f.Qty, 0m, OrderStatus.Filled)` and calls `_onReport`. Bound the lane (`laneCapacity`, `TryWrite`, critical-log on overflow). `DisposeAsync` completes the lane + awaits the worker. Use `IsTrueShutdown`-style OCE handling in the worker loop.

- [ ] **Step 4: Run to verify pass** → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/.../InteractiveBrokers/IbOrderGateway.cs tests/.../InteractiveBrokers/IbOrderGatewayTests.cs tests/.../InteractiveBrokers/FakeIbOrderClient.cs
git commit -m "feat(livehost): IbOrderGateway place/cancel + ack awaiter + off-pump ExecutionReport lane"
```

---

## PHASE C — Per-account adapters

### Task C1: `IbExchangeOrderClient : IExchangeOrderClient`

**Files:**
- Create: `src/.../InteractiveBrokers/IbExchangeOrderClient.cs`
- Test: `tests/.../InteractiveBrokers/IbExchangeOrderClientTests.cs`

**Interfaces:**
- Consumes: `IbOrderGateway.Place/Cancel`, `IbContractResolver` (Plan 1) for `symbol → ResolvedIbContract`.
- Produces: `IExchangeOrderClient` — `PlaceOrderAsync(symbol, side, type, qty, price?, stop?, ct)` → `gateway.Place(account, contract, request)` → `ExchangeOrderResult(id, [])`; `CancelOrderAsync(symbol, id, ct)` → `gateway.Cancel(id)`. `GetOpenOrdersAsync(symbol)` returns the gateway's current pushback snapshot filtered by symbol (Task E1 supplies it); `CancelAllOpenOrdersAsync(symbol)` cancels each.

- [ ] **Step 1: Write the failing test**

```csharp
public sealed class IbExchangeOrderClientTests
{
    [Fact]
    public async Task PlaceOrder_MapsStopToAuxPrice_TifDay_AccountTagged_ReturnsIdEmptyFills()
    {
        var ct = TestContext.Current.CancellationToken;
        var gw = Substitute.For<IIbOrderGateway>(); // extract an interface over Place/Cancel for mockability
        gw.Place("DU1", Arg.Any<ResolvedIbContract>(), Arg.Any<IbOrderRequest>(), ct).Returns(777L);
        var client = IbClientFixture.Build(gw, account: "DU1");

        var result = await client.PlaceOrderAsync("AAPL", OrderSide.Sell, OrderType.Stop, 3m, price: null, stopPrice: 95.0m, ct);

        Assert.Equal(777L, result.OrderId);
        Assert.Empty(result.Fills);
        await gw.Received(1).Place("DU1", Arg.Any<ResolvedIbContract>(),
            Arg.Is<IbOrderRequest>(r => r.OrderType == "STP" && r.AuxPrice == 95.0 && r.Action == "SELL"), ct);
    }
}
```

(Extract `internal interface IIbOrderGateway` over `Place`/`Cancel` so the client is unit-testable; `IbOrderGateway` implements it.)

- [ ] **Step 2: Run to verify failure** → FAIL.

- [ ] **Step 3: Implement** the mapping: `Market→"MKT"` (no prices), `Limit→"LMT"`(`LmtPrice=price`), `Stop→"STP"`(`AuxPrice=stop`); `Action = side == Buy ? "BUY" : "SELL"`; `Account` = ctor account; resolve the contract via `IbContractResolver`. Return `new ExchangeOrderResult(await gateway.Place(...), [])`.

- [ ] **Step 4: Run to verify pass** → PASS.

- [ ] **Step 5: Commit** `feat(livehost): IbExchangeOrderClient maps Domain orders to IB (MKT/LMT/STP, Tif=DAY, account-tagged)`

---

### Task C2: `IbAccountFundsSource : IAccountFundsSource`

**Files:** Create `src/.../InteractiveBrokers/IbAccountFundsSource.cs`; Test `IbAccountFundsSourceTests.cs`.

**Interfaces:** Consumes a small internal `reqAccountSummary` seam (mockable, mirroring `IIbMarketDataClient`); Produces `Task<AccountFunds> DiscoverFunds(Asset asset, ct)` returning `new AccountFunds(scale.AmountToTicks(availableFunds), currencyOf(asset))`.

- [ ] **Step 1:** Write the failing test: given a fake account-summary client returning AvailableFunds=10000 USD, `DiscoverFunds(aaplEquityAsset)` → `FreeScaled == new ScaleContext(asset).AmountToTicks(10000)`, `QuoteAsset == "USD"`.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement over the account-summary seam; scale with `new ScaleContext(asset)`.
- [ ] **Step 4:** Run → PASS.
- [ ] **Step 5:** Commit `feat(livehost): IbAccountFundsSource via reqAccountSummary`.

---

### Task C3: Neutralize `BinanceAccountTargetFactory` → `AccountTargetFactory`

**Files:**
- Rename/Create: `src/.../Live/AccountTargetFactory.cs` (move out of `Binance/`, drop the `Binance` prefix).
- Delete: `src/.../Live/Binance/BinanceAccountTargetFactory.cs`.
- Modify: `BinanceLiveConnector.cs` (construct the neutral factory with Binance providers).
- Modify: `tests/.../Live/BinanceAccountTargetFactoryTests.cs` → `AccountTargetFactoryTests.cs`.

**Interfaces:**
- Produces: `AccountTargetFactory(Func<string, Asset, IAccountFundsSource> fundsFor, Func<string, Asset, IExchangeOrderClient> clientFor, IOrderValidator orderValidator, ILogger logger, int channelCapacity) : IAccountTargetFactory`. `Create(account, asset, ct)` calls `fundsFor(account, asset).DiscoverFunds(asset, ct)` → seed Portfolio; `clientFor(account, asset)` → the account's `IExchangeOrderClient`; builds `LiveOrderContext` + `AccountTarget` exactly as today.

Rationale: Binance passes constant providers (its single funds source + single client); IB passes `(account, asset) => new IbExchangeOrderClient(account, sharedGateway, resolver)` and `(account, asset) => new IbAccountFundsSource(...)`. The Portfolio/LiveOrderContext/AccountTarget construction is shared — no parallel factory.

- [ ] **Step 1:** Update the renamed test to construct `AccountTargetFactory` with provider lambdas returning the existing NSubstitute funds source + client; assert `Create` seeds `Portfolio.InitialCash` from discovered funds (unchanged assertion).
- [ ] **Step 2:** Run → FAIL (type missing).
- [ ] **Step 3:** Create `AccountTargetFactory` with the generalized ctor; body = today's `BinanceAccountTargetFactory.Create` with `funds`/`orderClient` resolved via the providers. Update `BinanceLiveConnector.ConnectAsync` to build it with constant providers (`(_, _) => _fundsSource`, `(_, _) => _apiClient`).
- [ ] **Step 4:** Run the factory + Binance regression suites → PASS.
- [ ] **Step 5:** Commit `refactor(livehost): neutralize AccountTargetFactory (per-account funds/client providers)`.

---

### Task C4: Neutralize `BinanceMarketDataSource` → `DispatchMarketDataSource` (shared)

`BinanceMarketDataSource` is already fully venue-neutral (delegates `Register`/`EnsureSources`/`RecentBars`/`RemoveSources` to `IStrategyDispatch`/`ITickRouter`, zero Binance vocab). Per the reuse directive (same principle as C3's factory), **rename and share it** rather than create a duplicate IB copy.

**Files:**
- Rename/Create: `src/.../Live/DispatchMarketDataSource.cs` (move out of `Binance/`, drop the `Binance` prefix; body unchanged).
- Delete: `src/.../Live/Binance/BinanceMarketDataSource.cs`.
- Modify: `BinanceLiveConnector.cs` (construct `DispatchMarketDataSource`).
- Modify: `tests/.../Live/BinanceMarketDataSourceTests.cs` → `DispatchMarketDataSourceTests.cs`.
- (D1 wires the same `DispatchMarketDataSource` for `IbLiveConnector`.)

**Interfaces:** `DispatchMarketDataSource(IStrategyDispatch dispatch, ITickRouter tickRouter) : IMarketDataSource` — identical body to today's `BinanceMarketDataSource`, neutral name + namespace (`...Live`, not `...Live.Binance`).

- [ ] **Step 1:** Rename the test type to `DispatchMarketDataSourceTests`; add the 2 missing delegation asserts (`RecentBars`/`RemoveSources` → `tickRouter` `Received(1)`) flagged in the Plan 2 minor roll-up (T6). Keep `EnsureSources`/`Register` asserts.
- [ ] **Step 2:** Run → FAIL (type missing).
- [ ] **Step 3:** Move the file to `Live/DispatchMarketDataSource.cs`, rename the type, change the namespace to `AlgoTradeForge.LiveHost.Infrastructure.Live`; update `BinanceLiveConnector.ConnectAsync` to `new DispatchMarketDataSource(_dispatch, _tickRouter)`.
- [ ] **Step 4:** Run the renamed test + the Binance regression suites → PASS.
- [ ] **Step 5:** Commit `refactor(livehost): neutralize BinanceMarketDataSource → DispatchMarketDataSource (shared)`.

---

## PHASE D — Wiring

### Task D1: `IbLiveConnector` composition root + DI

**Files:**
- Create: `src/.../InteractiveBrokers/IbLiveConnector.cs`
- Modify: `src/.../InteractiveBrokers/IbDataPlaneServiceCollectionExtensions.cs` (add order-plane registration, or a sibling extension), keeping IB internals `internal` behind the public extension.
- Test: `tests/.../InteractiveBrokers/IbLiveConnectorTests.cs`

**Interfaces:**
- Consumes: one shared `IbSession`/`IbConnection`/`IbWrapper` (Plan 3), `IbOrderGateway` (B4), neutral `AccountTargetFactory` (C3) with IB providers (C1/C2), `DispatchMarketDataSource` (C4), `OrderRouter`, `LiveSessionDispatcher` (A2), `IbContractResolver` (Plan 1).
- Produces: an `ILiveConnector`-shaped composition root (match the `ILiveConnector` surface the host expects — `ConnectAsync`/`AddSessionAsync`/`RemoveSessionAsync`/`StopAsync`/`Status`/`AccountName`). `ConnectAsync` connects the shared `IbSession` (seeds order id), builds the order gateway + router + dispatcher, wires `gateway.onReport → dispatcher.OnExecutionReport`, subscribes `IbSession.Reconnected`. `AddSessionAsync` resolves the per-session quote currency (from the contract/funds) then `dispatcher.AddSession(config, quoteAsset, ct)`.

- [ ] **Step 1:** Write a test (using fakes for `IIbOrderClient` + a fake market-data session) asserting: `ConnectAsync` → `Status == Running`; `AddSessionAsync` resolves a target and binds the strategy's order context; a synthetic `execDetails` for a placed order drives the strategy's `OnTrade`. Mirror the structure of `IbVenueConnectorTests` + `MultiAccountRoutingTests`.
- [ ] **Step 2:** Run → FAIL.
- [ ] **Step 3:** Implement the composition root (no new behavior — wiring only; lean on the dispatcher for all session/report logic).
- [ ] **Step 4:** Add the DI extension; gate Binance-vs-IB on the existing `Venue` key (Plan 3's `VenueSelector`). Build the slnx.
- [ ] **Step 5:** Run → PASS; full Infrastructure suite green.
- [ ] **Step 6:** Commit `feat(livehost): IbLiveConnector composition root sharing the IbSession socket (data + orders)`.

---

## PHASE E — Reconnect reconciliation + gated paper

### Task E1: Per-target **union** reconcile + `ReconcileFromSnapshot`

**Files:**
- Modify: `src/.../Live/LiveSessionDispatcher.cs`
- Modify: `src/.../InteractiveBrokers/IbOrderGateway.cs` (feed the pushback snapshot on `Reconnected`)
- Test: `tests/.../Live/LiveSessionDispatcherUnionReconcileTests.cs`

**Interfaces:**
- Produces on `LiveSessionDispatcher`: generalize the reconcile to compute **expected = union of `GetExpectedOrders()` across every session bound to a target**, then diff via `OrderGroupReconciler.DetectAsync` and `CancelOrphansAsync`. Add `Task ReconcileFromSnapshot(string account, IReadOnlyList<long> brokerOpenOrderIds, CancellationToken ct)` for the IB reconnect path (diffs the account-wide snapshot against the union; the orphan set is `snapshot − union`).
- `IbOrderGateway` on `Reconnected`: `var snap = await wrapper.BeginOpenOrderSnapshot();` (after the re-establish triggers IB's pushback) → group by account → `dispatcher.ReconcileFromSnapshot(account, ids, ct)`.

**Open-point #5 resolution:** reconnect reconciliation uses **open-order pushback only**; `reqExecutions` is NOT called (avoids the double-apply risk that `execId` dedup would otherwise guard). Fills *missed during a disconnect* are a documented gap (deferred), analogous to Plan 3's data-plane catch-up.

- [ ] **Step 1: Write the failing co-tenancy test (the #8 guard)**

```csharp
[Fact]
public async Task ReconcileFromSnapshot_DoesNotCancelCoTenantWorkingOrders()
{
    var ct = TestContext.Current.CancellationToken;
    var f = await UnionReconcileFixture.TwoSessionsOnOneAccount(ct);
    // session A expects SL #1001; session B expects SL #2002; broker pushback has both + a true orphan #3003
    await f.Dispatcher.ReconcileFromSnapshot(f.Account, [1001, 2002, 3003], ct);

    Assert.DoesNotContain(1001, f.CancelledOrderIds); // A's order survives
    Assert.DoesNotContain(2002, f.CancelledOrderIds); // B's order survives
    Assert.Contains(3003, f.CancelledOrderIds);        // only the true orphan is cancelled
}
```

- [ ] **Step 2: Run → FAIL** (`ReconcileFromSnapshot` not defined / cancels co-tenant orders).
- [ ] **Step 3: Implement** the union: gather `expected` across `_sessions` whose `Target == the account's target` via each session's `ITradeRegistryProvider.TradeRegistry.GetExpectedOrders()` (snapshot on each session's `EventQueue` as the existing `ReconcileSession` does, to stay serialized), union them, then `orphanIds = brokerOpenOrderIds.Except(expectedExchangeIds)` and `CancelOrphansAsync`. Generalize the periodic `ReconcileSession` to the same union (Binance degenerate = single session → unchanged behavior).
- [ ] **Step 4: Run** the new test + the Plan 2 reconciliation/regression suites → PASS.
- [ ] **Step 5: Commit** `fix(livehost): per-target union reconcile + IB reconnect ReconcileFromSnapshot (co-tenancy safe)`.

---

### Task E2: Gated paper integration tests

**Files:** Create `tests/.../InteractiveBrokers/IbOrderPlanePaperTests.cs` (`[Trait("Category","IbPaper")]`, skip unless `IB_PAPER_HOST`/`IB_PAPER_PORT`/`IB_PAPER_CLIENT_ID` set — mirror `IbDataPlanePaperTests`).

**Interfaces:** Consumes the real `IbLiveConnector` against the gnzsnz paper gateway.

- [ ] **Step 1: Write the gated tests** (skipped in CI; runnable against paper):
  - `MarketOrder_Fills` — place MKT BUY 1 AAPL → assert the strategy's `OnTrade` fires and the Portfolio position updates.
  - `LimitOrder_SubmittedThenCancelled` — place LMT far from market → assert Submitted; cancel → assert Cancelled.
  - `TradeRegistryGroup_LifecycleAgainstPaper` — run a TradeRegistry group (entry MKT fills → SL+TP placed as individual orders → cancel one → sibling cancelled).
  - `ReconnectReconciliation` — place a resting order; force a reconnect; assert the pushback snapshot reconciles without cancelling the resting order.
  - `SharedSocket_NoOrderStarvation` — subscribe ticks + place an order under a tick flood; assert the fill is dispatched promptly (off-pump lane).
- [ ] **Step 2: Run gated (skipped in CI)** — Run: `dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/ --filter "Category!=IbPaper"` to confirm the suite stays green with these skipped.
- [ ] **Step 3: Commit** `test(livehost): gated IB paper order-lifecycle + reconnect + shared-socket tests`.

---

## Self-Review

**Spec coverage:** Decision 1 (account-aware/single-live) → C1/C2/C3 + E2; Decision 2 (await-ack/push) → B4 (`Place` awaits ack) + C1 (`(id, [])`) + A2 (`OnExecutionReport` push); Decision 3 (extract) → A1/A2; Decision 4 (individual-order-only/brackets strategy-side) → C1 (no bracket method) + E2 (TradeRegistry lifecycle); Decision 5 (gross-at-emit/execId-dedup) → B3 (`MarkExecSeen`, commission deferred) + B4. Error-handling #1 (off-pump lane) → B4; #2 (ack-timeout) → B4; #3 (reject) → B3/B4; #4 (dedup bound) → B3; #5 (re-arm) → B1; #6 (warnings) → B3; #7 (contract reject) → C1; #8 (co-tenancy union) → E1. Components: `ExecutionReport`/`LiveSessionDispatcher` (A), `NextOrderId` (B1), `IbWrapper` order callbacks (B3), `IbOrderGateway` (B4), `IbExchangeOrderClient` (C1), `IbAccountFundsSource` (C2), `AccountTargetFactory` (C3), `DispatchMarketDataSource` (C4), `IbLiveConnector` (D1). Tests #1-#11 map to B1/B3/B4/C1/E1/D1/E2.

**Placeholder scan:** The IBApi callback arities (`orderStatus` 11-arg, `execDetails`, `commissionAndFeesReport`, `error`) and the exact `RejectCodes` set MUST be confirmed against the vendored `src/AlgoTradeForge.IbApi` `EWrapper` during B3 — flagged inline, not deferred work. The `IIbOrderGateway` interface extraction (C1) and the account-summary seam (C2) are named, not vague.

**Type consistency:** `ExecutionReport`/`ExecType` (A1) consumed identically in A2/B4; `NextOrderId`/`SeedNextOrderId` (B1) consumed in B4/D1; `IbOrderRequest`/`IIbOrderClient` (B2) consumed in B4/C1; `ReconcileFromSnapshot(string, IReadOnlyList<long>, ct)` (E1) consumed by `IbOrderGateway` (E1). `AccountTargetFactory` provider signatures (C3) consumed in D1.

## Execution Handoff

(See below.)
