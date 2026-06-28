# LiveHost — Plan 2: `IOrderRouter` + multi-account routing — Design

**Date:** 2026-06-27
**Status:** Approved (brainstorming) — proceeds to writing-plans → SDD
**Spine:** `docs/superpowers/specs/2026-06-25-livehost-ib-replan-phase-design.md` (IB re-plan, Plan 2 of `1 → {2,3} → 4 → 5`)
**Depends on:** Plan 1 (venue-neutral contract identity) — MERGED
**Review tier:** opus (order-path / order-integrity critical)

## Scope

De-conflate the **order side** of the LiveHost into explicit, account-keyed seams so a single market-data transport can fan orders to multiple isolated broker accounts, each scaling prices/quantities off its **own** asset. Binance (a `Concurrent` venue) becomes the degenerate one-account/one-source case it always was; the multi-account model is validated against a fake venue. The real IB single-socket shared-data cohabitation is **deferred to Plan 4** — this plan builds the reusable spine Plan 4 plugs into.

**Acceptance (from the spine):** a two-account routing test (strategy set X → account A, set Y → account B, shared market data, orders isolated per account) plus per-target scaling correctness.

## Context — what exists today

`BinanceLiveConnector` is a single fused object playing four roles at once: **transport** (WS / user-data stream), **market-data source** (`ITickRouter` + `IStrategyDispatch` + shared bar sources), **account**, and **order-routing target** (`Portfolio` + `LiveOrderContext` + `IExchangeOrderClient` + reconciliation). For Binance this is fine: an account *is* a separate login *is* a separate transport (1:1:1:1). `ILiveAccountManager` keys one connector per account, and "route strategy → account" already happens in `StartLiveSessionCommandHandler` by selecting the connector.

IB inverts this: **one** login/socket holds **many** sub-accounts. Orders from different strategy sets must route to different accounts over a shared transport and shared market data. Two assumptions in the current code encode the single-account/single-scale world and must go:

- `LiveOrderContext.cs:225` — `new ScaleContext(_primaryAsset)` scales **every** order by the session's one asset, regardless of `order.Asset`.
- `BinanceLiveConnector.cs:325` — `_tickRouter.EnsureSources(registration, _ => sessionScale)`: the per-instrument scale resolver seam already exists, but is fed a **constant** session scale.

**Owner directive (carried from the spine):** NOT in production. Break anything for the cleanest, most extensible, maintainable end-state — no back-compat shims, no dead bridges. "Clean" still means correct + tested.

## Locked decisions (this brainstorming)

1. **Two seams, bound by a router.** Split the order side into `IAccountTarget` (account-scoped order seam) and a thin `IMarketDataSource` (transport-scoped, names the already-shared data plane), bound by `IOrderRouter`. Binance = 1 source : 1 target (degenerate); IB = 1 source : N targets (Plan 4). The connector becomes a thin **composition root** owning these units.
2. **Account-grain targets.** An `IAccountTarget` == one broker account: one `Portfolio`, one `LiveOrderContext`, one reconciliation scope, one `IExchangeOrderClient`. It scales each order off **that order's** asset (`new ScaleContext(order.Asset)`) — *not* a single session asset. (Rejected: `(account, asset)`-grain targets, which fragment account-wide cash/margin/reconciliation; rejected: scale derived ad-hoc at each call site, which scatters the concern.)
3. **Shared, broker-true `Portfolio`.** When two sessions route to the same account they **share** one `Portfolio` (shared cash, positions, margin) — matching the broker reality that an account has one balance. The `Portfolio` is **seeded from the real broker free funds discovered at first attach** (`GetAccountInfoAsync`), never a configured absolute. Per-strategy capital is a **fraction of live free funds that the strategy self-limits against** — expressed as a strategy parameter (per-deployment, via `StrategyParameters`), *not* a framework-enforced budget. The order context reports account-wide `AvailableMargin` (which already abstracts cash-account vs margin-account); a strategy sizes as `fraction × AvailableMargin`. (Rejected: a configured absolute `InitialCash`, which can't track varying free funds across co-tenant strategies; rejected: per-session sub-portfolios / framework-enforced budgets, which require per-strategy P&L attribution within a netted broker account — deferred Option-1.)
4. **Plan 2 substrate = seam + Binance degenerate refactor + fake-venue multi-account test.** Binance is refactored to compose via the new seams (proving the degenerate case through them); the N>1 fan-out is proven against in-memory / NSubstitute doubles. Real IB shared-socket fan-out is Plan 4.
5. **Thin `IMarketDataSource` now.** Plan 2 formalizes the shared-data seam as a thin wrapper over the existing `IStrategyDispatch` + `ITickRouter`; Plan 3's `IbSession` implements it for real. (This is the honest other half of decision 1, kept minimal — not a Plan-3 land-grab.)
6. **`ILiveAccountManager` keeps account-keying for Plan 2.** For Binance, account == transport == connector still holds (1:1), so the manager and the WebApi status/list endpoints are unchanged; the N>1 fan-out lives *inside* the connector via router + targets. IB's account < transport re-keying is Plan 3/4's concern, designed against the real `IbSession`.

## Components

### New seams — interfaces in `LiveHost.Application.Live` (beside `ITickRouter`, `IExchangeOrderClient`)

**`IAccountTarget`** — account-scoped order seam (one per broker account):

```csharp
public interface IAccountTarget : IAsyncDisposable
{
    string AccountName { get; }
    IOrderContext OrderContextFor(Guid sessionId);   // per-session facade over the shared ledger
    Portfolio Portfolio { get; }                     // for snapshots / reconciliation
    // born running (factory.Create starts the order-drain tasks); torn down via DisposeAsync():
    //   graceful flush of queued orders/cancels → cancel-all open orders → stop tasks. Idempotent.
}
```

Owns: one `Portfolio`, the account-scoped `LiveOrderContext` (channels, pending-orders, `_nextOrderId` — all account-level), the account's `IExchangeOrderClient`, and its reconciliation scope.

**Per-session order-context facade.** The Domain `IOrderContext.Submit(order)` signature carries no session id, but an account-scoped context shared by N sessions must know *which* session placed an order (so the right strategy gets `OnTrade`). `OrderContextFor(sessionId)` returns a thin **`SessionOrderContext`** facade bound to that session id: its `Submit`/`Cancel` delegate to the shared account `LiveOrderContext`, tagging the order's originating session (recorded `exchangeOrderId → sessionId` via the router on re-key). All heavy state (order channels, pending-order map, `Portfolio`, id counter) is account-level and shared; only the session tag is per-facade. For Binance (one session per account) the facade is a trivial pass-through.

**Lifecycle (decoupled from session lifecycle).** A target is the broker *account*, not a strategy session — it outlives the sessions that come and go on it, so it is **reference-counted**. `factory.Create` returns it **already running** (its order/cancel channel-drain tasks started, linked to the connector's CTS token). Teardown is `DisposeAsync()` — the graceful path lifted from today's `LiveOrderContext.StopAsync` (complete channels → await processing tasks so in-flight orders flush → cancel open orders on the exchange → cancel CTS). `DisposeAsync` takes no token by design: a caller's cancellation must not abort the flush mid-teardown. The router/connector disposes a target only when its **last** session leaves, and disposes all targets on connector shutdown. Per-session init/deinit (bind `strategy ↔ target.OrderContext`, register with the source, the session's own queues + drain task) is a **separate** concern owned by the composition root, not by `IAccountTarget`.

**`IOrderRouter`** — binds sessions to targets, routes inbound reports:

```csharp
public interface IOrderRouter : IAsyncDisposable
{
    Task<IAccountTarget> ResolveTarget(string account, CancellationToken ct = default);  // get-or-create; refcount++
    Task ReleaseTarget(string account, CancellationToken ct = default);                  // refcount--; DisposeAsync on 0
    void TrackOrder(long exchangeOrderId, Guid sessionId);
    bool TryResolveSession(long exchangeOrderId, out Guid sessionId);
    IReadOnlyCollection<IAccountTarget> Targets { get; }
}
```

`ResolveTarget` is **get-or-create** (the factory discovers the account's real free funds on first creation; concurrency-safe — see Error Handling) and increments the account's session **refcount**; `ReleaseTarget` decrements it and `DisposeAsync`s the target on the last release. `DisposeAsync` on the router disposes all live targets (connector shutdown). The router generalizes today's `_sessions` + `_binanceOrderToSession` maps, keeping **order→session** (for `OnTrade` dispatch to the originating strategy) distinct from order→account (which target owns the shared `Portfolio`).

**`IAccountTargetFactory`** — venue-neutral creation seam injected into the router:

```csharp
public interface IAccountTargetFactory
{
    Task<IAccountTarget> Create(string account, CancellationToken ct = default);
}
```

Venue-specific impls (`BinanceAccountTargetFactory` resolves `BinanceLiveOptions.Accounts[name]` → `BinanceApiClient` → `IExchangeOrderClient`, **discovers** the account's real free funds via `GetAccountInfoAsync` to seed the `Portfolio`) keep `IOrderRouter` free of any venue vocabulary. The fake-venue test factory returns an in-memory target seeded with a fixed test balance.

**`IMarketDataSource`** (thin) — names the already-shared data plane for a transport:

```csharp
public interface IMarketDataSource
{
    void Register(LiveSessionRegistration registration);
    ValueTask EnsureSources(LiveSessionRegistration reg, Func<string, ScaleContext> scaleFor);
    IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec);
    ValueTask RemoveSources(Guid sessionId);
}
```

In Plan 2 it wraps `IStrategyDispatch` + `ITickRouter`; Plan 3's `IbSession` implements it for real.

### Per-asset / per-instrument scaling (no cache type — `ScaleContext` is a zero-alloc struct)

`ScaleContext` is a `readonly record struct` (three `decimal`s), so `new ScaleContext(asset)` allocates nothing on the heap — a cache buys nothing. Each leaky constant is replaced directly, off the *right* asset:

- order path: `LiveOrderContext.cs:225` `new ScaleContext(_primaryAsset)` → `new ScaleContext(order.Asset)` (the order carries its own asset; no lookup needed).
- data path: `BinanceLiveConnector.cs:325` `_ => sessionScale` → `instrument => instrumentScales[instrument]`, where `instrumentScales` is a `Dictionary<string, ScaleContext>` built once per `AddSessionAsync` from the registration's subscriptions (each subscription's instrument → `new ScaleContext(subscription.Asset)`).

### Implementations in `LiveHost.Infrastructure.Live`

`AccountTarget : IAccountTarget`, `SessionOrderContext : IOrderContext` (per-session facade), `OrderRouter : IOrderRouter`, `BinanceAccountTargetFactory : IAccountTargetFactory`, `BinanceMarketDataSource : IMarketDataSource`.

### Refactored existing code

- **`LiveOrderContext`** — per-session → per-account. Ctor drops `_primaryAsset`; each order scales off `order.Asset` (`new ScaleContext(order.Asset)`). Now owned by `AccountTarget`. Channel/pending-order/fill mechanisms unchanged in shape, account-scoped in lifetime. Portfolio access made multi-writer-safe (see Error Handling).
- **`BinanceLiveConnector`** — becomes the composition root: owns 1 `IMarketDataSource` + 1 `IOrderRouter` (which owns 1 `AccountTarget` for Binance). `AddSessionAsync` delegates to `ResolveTarget` → bind `strategy ↔ target.OrderContextFor(sessionId)` → `source.Register` + `EnsureSources(instrument => instrumentScales[instrument])`. The `_sessions` / `_binanceOrderToSession` / reconciliation-loop logic moves to the router/targets; the connector keeps WS lifecycle + `ConnectAsync` / `StopAsync` orchestration.
- **`OrderGroupReconciler`** — run per-`AccountTarget` (the broker-correct, per-account open-order-pushback scope).
- **`LiveSessionConfig.AccountName`** — already carries the spine's `executionAccount`; kept as-is (no contract churn).
- **`StartLiveSessionCommand` / `LiveSessionConfig` / `StartLiveSessionRequest`** — drop the absolute `InitialCash` field (and the handler's `scale.AmountToTicks(InitialCash)` step). The account ledger is seeded from discovered broker free funds; per-strategy capital is a strategy parameter. `Portfolio.InitialCash` now records the discovered starting equity (still meaningful for snapshots).

## Data flow

### A. Session start — outbound binding + target get-or-create

```
StartLiveSessionCommandHandler  (resolves asset, scales params, builds LiveSessionConfig — no InitialCash)
   ▼
connector.AddSessionAsync(config)                       (composition root)
   ├─ target = await orderRouter.ResolveTarget(config.AccountName, ct)
   │      └─ get-or-create (per-account async gate): new → factory.Create returns a BORN-RUNNING
   │         target (Portfolio seeded from DISCOVERED broker free funds; account-scoped LiveOrderContext;
   │         IExchangeOrderClient; order-drain tasks started on ct).  existing → reuse live target.
   │         refcount += 1
   ├─ bind strategy ↔ target.OrderContextFor(sessionId)  (IOrderContextReceiver.SetOrderContext; per-session facade)
   ├─ source.Register(registration)
   └─ source.EnsureSources(registration, instrument => instrumentScales[instrument])
                                  ▲ instrumentScales: Dictionary<string,ScaleContext> from subscriptions — replaces _ => sessionScale
```

### B. Outbound order — strategy → exchange (per-asset scaling)

```
strategy → orderContext.Submit(order)                   (order.Asset may differ from any "primary")
   ▼ LiveOrderContext.ProcessOrdersAsync
   scale = new ScaleContext(order.Asset)                ◄── was new ScaleContext(_primaryAsset)  (:225)
   price/stop = scale.ToMarketPrice(...)
   result = _orderClient.PlaceOrderAsync(order.Asset.Name, side, type, qty, price, stop)
   re-key local→exchange id;  orderRouter.TrackOrder(exchangeId, sessionId)   ◄── order→session
   process REST fills → AddFill → shared account Portfolio.Apply
```

### C. Inbound execution report — exchange → correct target + originating strategy

```
venue user-data stream → OnExecutionReport(report)      (on the shared transport)
   ├─ orderRouter.TryResolveSession(report.OrderId, out sessionId)
   │      └─ unmapped? buffer (existing _bufferedReports), replay on TrackOrder
   ├─ target = the AccountTarget owning that order        (account-scoped shared Portfolio)
   ├─ enqueue on the SESSION's EventQueue (exec priority):
   │      fill = new Fill(..., new ScaleContext(report.Asset).FromMarketPrice(price), ...)
   │      target.OrderContext.AddFill(fill) → shared Portfolio.Apply
   │      originatingStrategy.OnTrade(fill, order)         ◄── order→session: the RIGHT strategy notified
```

The fill applies to the account's **shared** `Portfolio`, while `OnTrade` fires on the session that **placed** the order — even under co-tenancy.

### D. Reconciliation, removal, stop — per-account scope

- **Reconcile loop:** iterate `orderRouter.Targets`; per target run the existing 3-phase `OrderGroupReconciler` against that account's server-side open orders.
- **RemoveSession:** unregister from source, drain the session's queues, decrement the target's refcount; the `AccountTarget` is `DisposeAsync`d (flush + cancel-all open orders) only when its **last** session leaves (a target = a broker account, outliving any single session).
- **StopAsync:** drain all sessions → `DisposeAsync` all targets → cancel CTS → WS teardown, preserving today's ordering invariants (drain-before-cancel) but iterating targets for the order side.

## Error handling (failure modes introduced by this refactor)

Existing guards carry over unchanged: bounded channels (`EventQueue` `FullMode.Wait`, `MarketDataQueue` `DropNewest`), `IsTrueShutdown(ex, ct)` in long-running loops, OCE filters, buffered-report replay.

1. **Race-safe target get-or-create.** Concurrent `ResolveTarget` for the same account must create exactly one target. The factory does async balance validation, so a bare `GetOrAdd` is unsafe; creation is guarded by a per-account async gate (`using var _ = await gate.LockAsync(ct)`), double-checking the map inside the lock. A creation that throws (bad creds / failed validation) leaves no partial map entry and propagates so `StartLiveSessionCommandHandler`'s `sessionStore.Remove` rollback fires.
2. **No per-session cash to reconcile.** Account funds are discovered account-wide from the broker, not configured per session, so there is no absolute `InitialCash` to validate or reconcile across co-tenant sessions — a later session simply reuses the live account `Portfolio`. Capital allocation is a strategy-self-limited fraction (a strategy parameter), entirely outside the order-integrity path.
3. **Shared-Portfolio multi-writer safety (order-integrity heart).** `LiveOrderContext` today assumes a single writer. Account-grain co-tenancy lets two session processing tasks touch the same `Portfolio` — one applying a fill while another reads `Cash`/`AvailableMargin` to validate a `Submit`. The existing `_recentFillsLock` guards the write; Plan 2 closes the gap by routing **validation reads and fill writes through one lock**, so a submit sees either pre- or post-fill balance, never torn state. Correct-by-construction; the two-account test (distinct accounts) does not stress it, so a dedicated co-tenancy concurrency test carries the guarantee.
4. **Reference-counted teardown resilience.** `target.DisposeAsync()` (cancel open orders + stop order channels) fires on last-session-leaves, never first. Each target's `DisposeAsync` is wrapped in its own `try/catch` (mirroring today's per-entry `StopAsync`) so one target's cancel-all failure cannot abort the others; the exchange-side safety-net cancel-all remains the backstop. `DisposeAsync` is idempotent (a second dispose is a no-op), covering the case where both a last-session-removal and a connector shutdown race to tear the same target down.
5. **Unknown-instrument scale — asymmetric by path.** `assetFor(instrument)` resolves from the registration's subscriptions. Order path with no resolvable scale → hard error, reject the order (never guess a scale on a money path). Data path with an unmapped instrument → log + drop (consistent with `DropNewest`); never throw on the shared single-reader drain task, which would tear down every session on the transport.
6. **Inbound report routing isolation.** Buffered-report replay (`_bufferedReports`) moves into the router but stays keyed by exchange order id (venue-global), so a report only ever reaches the target that owns its order — no cross-account leakage.

## Testing strategy

Test-First (Principle II): every new unit gets a failing xUnit test before implementation. NSubstitute for doubles. Multi-account tests use a fake `IAccountTargetFactory` (in-memory targets), per-account NSubstitute `IExchangeOrderClient`, and one shared `IMarketDataSource`.

| # | Test | Asserts | Guards |
|---|------|---------|--------|
| 1 | Per-instrument data-plane scale map | the `instrument => ScaleContext` resolver `AddSessionAsync` passes to `EnsureSources` returns each instrument's own scale (built from subscriptions); two instruments with different ticks → different scales | data-path scaling (`:325`) |
| 2 | Per-target scaling correctness | order on asset T1 → scaled by T1; order on T2 → scaled by T2; oracle proving old single-`_primaryAsset` scaling would mis-scale T2 | acceptance #2; kills `:225` + `:325` |
| 3 | Two-account routing isolation (headline) | X→A calls A's order client (not B's); Y→B calls B's; A's fill mutates only A's `Portfolio`; both sessions get the same bars from the shared source; inbound report for A's order → A's target **and** X's `OnTrade` (not Y's) | acceptance #1 |
| 4 | Co-tenancy shared `Portfolio` | two sessions → account A share one `Portfolio`; parallel submits+fills never tear cash/positions; `OnTrade` routes to the originating session | failure-mode 3 |
| 5 | Get-or-create race | N concurrent `ResolveTarget(A)` → exactly one target, factory `Received(1)` | failure-mode 1 |
| 6 | Account funds discovered, not configured | new account → `Portfolio.InitialCash` seeded from the factory-discovered free funds (NSubstitute broker balance), not from any request field; a second session on A reuses the same live `Portfolio` (no re-seed) | decision 3 / capital model |
| 7 | Reference-counted lifecycle | 2 sessions on A: remove #1 → target alive, not disposed, cancel-all not called; remove #2 → target `DisposeAsync`d once, cancel-all once; double-dispose is a no-op | failure-mode 4 |
| 8 | Unknown-instrument scale | order path → order `Rejected`; data path → dropped, drain task survives | failure-mode 5 |
| 9 | Binance degenerate regression | existing `BinanceLiveConnector` suite stays green through recomposition (1 source : 1 target) | behavior preserved |

Tests 3 and 4 are complementary by design: the headline test proves isolation across distinct accounts (which never share a `Portfolio`); the co-tenancy test is the one that exercises the multi-writer lock the account-grain model permits.

The exact LiveHost test project(s) are confirmed when writing the plan; the suite lands there.

## Out of scope (faithful boundaries)

- **Live IB / shared-socket fan-out** — Plan 4 (IB order session against IB paper).
- **`ILiveAccountManager` re-keying to transport** — Plan 3/4, designed against the real `IbSession`.
- **BenchmarkDotNet gate** — the named perf scenarios are backtest + optimization hot paths; the live order path is not one. No allocation concern: `ScaleContext` is a zero-alloc `readonly record struct`, so per-order/per-instrument construction needs no cache.
- **Multi-currency PnL** — inherited spine open point; USD-only.

## Verification

Done when: the three new seams (`IAccountTarget` + `SessionOrderContext`, `IOrderRouter`, thin `IMarketDataSource`) are implemented behind the interfaces above; both single-scale constants (`LiveOrderContext.cs:225`, `BinanceLiveConnector.cs:325`) are removed and replaced with per-asset / per-instrument `ScaleContext`; the nine-test suite is green; the existing LiveHost/Binance suites stay green (degenerate case through the new seams); and the owner signs off on the written design before the plan is executed.
