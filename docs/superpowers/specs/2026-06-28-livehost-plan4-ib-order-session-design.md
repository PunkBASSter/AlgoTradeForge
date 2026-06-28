# LiveHost — Plan 4: IB order session (execution) + single-socket cohabit — Design

**Date:** 2026-06-28
**Status:** Approved (brainstorming) — proceeds to writing-plans → SDD
**Spine:** `docs/superpowers/specs/2026-06-25-livehost-ib-replan-phase-design.md` (IB re-plan, Plan 4 of `1 → {2,3} → 4 → 5`)
**Depends on:** Plan 2 (`IOrderRouter` + multi-account) AND Plan 3 (`IbVenueConnector` data plane + single-session `IbSession`)
**Review tier:** opus (order-integrity + concurrency on the shared socket)

## Scope

Plug **Interactive Brokers order execution** into the account-keyed seam Plan 2 built, **sharing the one `IbSession` socket** Plan 3 built — so a single IB login both collects ticks and executes paper orders in one process (the entire reason the four-planes design exists). Plan 4 owns:

- **A neutral `ExecutionReport`** + an **extracted `LiveSessionDispatcher`** — the venue-neutral per-session dispatch core (exec-priority queues, `OnExecutionReport`→`AddFill`→`OnTrade`, buffered-report replay, the reconcile loop) lifted out of `BinanceLiveConnector` so both venues compose it.
- **The IB order seam** on the shared socket: a connection-scoped order-id allocator, order/fill/status callbacks in `IbWrapper`, and an **`IbOrderGateway`** that places/cancels orders, awaits the broker ack, maps IB callbacks → neutral `ExecutionReport` off the pump thread, and reconciles against IB's reconnect open-order pushback.
- **`IbExchangeOrderClient : IExchangeOrderClient`** (per account) + **`IbAccountFundsSource : IAccountFundsSource`** (funds via `reqAccountSummary`) feeding the **neutralized `AccountTargetFactory`** (renamed from `BinanceAccountTargetFactory`, no parallel IB factory), plus an **`IbMarketDataSource : IMarketDataSource`** so the extracted dispatcher's data-plane seam is satisfied for IB.
- **`IbLiveConnector`** — the composition root that cohabits Plan 3's data plane and the order plane on one `IbSession`, reusing `OrderRouter` unchanged.

**Owner directive (carried from the spine):** NOT in production. Break anything for the cleanest, most extensible, maintainable end-state — no back-compat shims, no dead bridges. "Clean" still means correct + tested. All IB types `internal`; the `IBApi` reference stops at the connector/translation seam; Domain stays venue-neutral with zero new ProjectReferences.

**Sequencing:** Plan 2 is not yet on `main` (it lives on `feat/livehost-plan2-order-router`, awaiting the owner's squash-merge). Plan 4 **branches off `main` AFTER Plan 2 lands** — the `LiveSessionDispatcher` extraction then refactors stable, merged code with the Binance suite as the regression guard.

## Brainstorming decisions (locked)

| # | Decision | Rationale |
|---|----------|-----------|
| 1 | **Account-aware seam, single-account live.** The IB order client/target/factory are genuinely account-keyed (sets IB `Order.Account`, discovers funds per account, routes by account), so Plan 2's multi-account seam IS exercised by IB. But only ONE paper account is required for the live E2E acceptance; the N>1 sub-account fan-out is unit-tested with simulated accounts and live-gated. | A standard gnzsnz paper gateway exposes a single account (`DUxxxxxx`), not FA sub-accounts. Building/validating FA infra paper can't exercise would block the endpoint for no gain; the seam is still structurally account-aware. |
| 2 | **Await ack, fills via the push path.** `PlaceOrderAsync` allocates the order id, writes `placeOrder`, awaits the FIRST broker ack (`orderStatus`/`openOrder`) with a bounded timeout — surfacing synchronous rejects as a thrown exception like Binance REST — and returns `ExchangeOrderResult(orderId, [])`. ALL fills flow through `execDetails` → `ExecutionReport` → dispatcher → `AddFill`+`OnTrade`. | IB never returns fills inline. One uniform fill path (no inline/push split, no double-count), clean reject surface, `LiveOrderContext`'s REST-fill block is simply skipped (empty fills). |
| 3 | **Extract the shared dispatch core in Plan 4.** Introduce a neutral `ExecutionReport` + a `LiveSessionDispatcher` both Binance and IB compose; zero duplication of the order-integrity-critical loop. | IB is the SECOND composition root — the duplicate-vs-extract question is now concrete. Owner directive favors the cleanest single-source end-state; extraction lands after Plan 2 merges, so it refactors stable code guarded by the LiveHost unit suites (`LiveOrderContextTests`/`OrderRouterTests`/`MultiAccountRoutingTests` + new `LiveSessionDispatcher` tests). |
| 4 | **Connector is individual-order-only; brackets stay strategy-side.** `IExchangeOrderClient` keeps just `PlaceOrderAsync`/`CancelOrderAsync`. `TradeRegistryModule` synthesizes the bracket (entry → SL+TP individual orders → client-side OCO → liquidation). The bracket *lifecycle* acceptance runs a TradeRegistry group against IB paper. | The identical `TradeRegistryModule` runs in backtest, Binance live, and IB live — one code path, one golden suite. IB `STP`/`LMT` are first-class individual orders. Native IB OCA (server-side one-cancels-all) is a deferred cross-venue robustness enhancement, not built here. |
| 5 | **Gross fill at emit, deduped by `execId`; commission deferred.** Emit the neutral fill on `execDetails` (price+qty) with **commission = 0**. IB delivers `commissionAndFeesReport` *after* `execDetails`, so it is never available at emit time — do not attempt a join-at-emit. A precise late-commission `Portfolio` cash-adjustment when the report lands is a flagged follow-up (open point #2), not built here. Never block the fill on commission. | A missing/delayed commission report must never strand a fill (a stuck position is catastrophic; a 0 commission is cosmetic and does not affect `TradeRegistryModule.ComputePnl`, which is gross). `execId` dedup (not `orderId`) is the load-bearing guard against double-applying a fill if reconnect reconciliation replays executions. (Earlier "join commission at emit" framing was dropped: IB's callback order makes it dead logic.) |

## Cross-cutting facts established (verified against source)

- **The socket is write-safe already.** `EClient.placeOrder` builds a **call-local** buffer (`EClient.cs:1250`) and `ESocket.Send` serializes on `tcpWriterLock` (`ESocket.cs:17`). Concurrent `placeOrder` + `reqTickByTickData` on the one socket are thread-safe at the IBApi transport — **no application-level socket write-lock is needed.** The "order plane independent of market data" invariant is therefore about the callback/dispatch side (pump-thread demux + exec-priority lanes), not the write side.
- **One reconnect authority.** There is exactly one drop signal (`IbWrapper.ConnectionDropped`) and one reconnect worker (`IbSession.ReconnectLoop`). The order plane does NOT own a second worker; it subscribes to `IbSession.Reconnected` to re-run per-account reconciliation. (Rejected alternative: a separate `IbOrderSession` with its own reconnect — two listeners racing one drop.)
- **Two id spaces on one transport.** `IbConnection` owns `NextReqId()` (market data, exists) and gains a sibling `NextOrderId()` (orders), seeded from `IbWrapper.NextValidId`. Because IB order ids are connection-global, the allocator is socket-scoped (on `IbConnection`), shared by all per-account order clients — NOT per-client (which would collide on the shared socket). **Seeding ordering matters:** `NextValidId` is a `Task<int>`; `IbConnection.Connect` already awaits it (`IbConnection.cs:57`), so the order-id counter is initialized from that awaited seed **at connect, before any `placeOrder` can run** — a synchronous `NextOrderId()` never races an unseeded counter. Reconnect re-arms the seed (extends `ResetForReconnect`).

## Architecture

```
                 ┌──────────────── IbSession (shared, internal) ─────────────────┐
                 │  IbConnection (1 EClientSocket + EReader pump)                 │
                 │    NextReqId()  +  NextOrderId() (seeded from NextValidId)     │
                 │  IbWrapper (1 callback sink): tick/bar sinks  +  ORDER sinks   │
                 │    (orderStatus/openOrder/openOrderEnd/execDetails/commission) │
                 │  reconnect worker (SOLE owner) → Reconnected event             │
                 └───────┬──────────────────────────────────┬────────────────────┘
       DATA PLANE (Plan 3)                              ORDER PLANE (Plan 4)
   IbVenueConnector / IbVenueBarSource              IbOrderGateway (socket-scoped)
       (push→channel→pull, ticks+bars)                place/cancel · NextOrderId
              │                                        ack awaiters · execId dedup
              │                                        IB cb → ExecutionReport
              │                                        order-event lane (off pump)
              │                                        Reconnected → reconcile
              ▼                                                 │
   RelayIngest.Pump → archival / dispatch          IbExchangeOrderClient : IExchangeOrderClient
                                                       (per account; tags Order.Account)
                                                                 │
   ┌─────────────────────── shared, venue-neutral ──────────────┴───────────────┐
   │  LiveSessionDispatcher  (extracted from BinanceLiveConnector)               │
   │    _sessions · LiveSessionEntry (exec EventQueue Wait + MarketDataQueue Drop)│
   │    DrainSessionQueues (exec priority) · OnExecutionReport→AddFill→OnTrade    │
   │    _bufferedReports replay · per-AccountTarget reconcile loop               │
   │  IOrderRouter / IAccountTarget / LiveOrderContext  (Plan 2, unchanged)      │
   └────────────────────────────────────────────────────────────────────────────┘

   IbLiveConnector = composition root: 1 IbSession (data + IbOrderGateway),
                     OrderRouter + AccountTargetFactory(IbAccountFundsSource, IB client provider),
                     1 IbMarketDataSource, 1 LiveSessionDispatcher.
   BinanceLiveConnector = composition root: WS transport + user-data → ExecutionReport,
                     OrderRouter + AccountTargetFactory(BinanceFunds, Binance client),
                     1 BinanceMarketDataSource, 1 LiveSessionDispatcher.
```

## Components

### A. Extracted venue-neutral core (new, `LiveHost.Application.Live` + `LiveHost.Infrastructure.Live`)

**`ExecutionReport`** (neutral record, `Application.Live`) — what both venues feed the dispatcher, shaped from today's `BinanceExecutionReport`:

```csharp
public sealed record ExecutionReport(
    long OrderId, Asset Asset, OrderSide Side, ExecType ExecType,   // New | Trade | Canceled | Rejected | Expired
    decimal LastFillPrice, decimal LastFillQty, decimal Commission, OrderStatus Status);
```

**`LiveSessionDispatcher`** (`Infrastructure.Live`) — lifted, behavior-preserving, from `BinanceLiveConnector`. Owns `_sessions`, `_removedSessions`, `_bufferedReports`, the per-session `LiveSessionEntry` (exec `EventQueue` `FullMode.Wait` + `MarketDataQueue` `DropNewest`), the `DrainSessionQueues` exec-priority loop, `OnExecutionReport(ExecutionReport)` → `TryResolveSession` → `AddFill` + `OnTrade`, and the per-`IAccountTarget` reconcile loop. Depends only on `IOrderRouter`, `IMarketDataSource`, `IInt64BarStrategy` — no venue type. `AddSessionAsync`/`RemoveSession`/`StopAsync` lifecycle moves here from the connector.

`BinanceLiveConnector` shrinks to: WS transport lifecycle + mapping its user-data stream → `ExecutionReport` → `dispatcher.OnExecutionReport`. The extraction is guarded by direct `LiveSessionDispatcher` unit tests plus the already-green `LiveOrderContextTests` (15), `OrderRouterTests` (5), `MultiAccountRoutingTests` (6) — the CI regression net (the `BinanceLiveConnectorE2ETests` are testnet-gated and skipped, so they do not guard the lift).

### B. IB order seam (new/grown, `Infrastructure.Live.InteractiveBrokers`, all `internal`)

**`IbConnection`** — add `int NextOrderId()`: a connection-scoped order-id source (sibling to `NextReqId()`), seeded from `IbWrapper.NextValidId` at each connect, re-armed on reconnect. One id per `Submit` (brackets are individual strategy-side orders — no consecutive-id reservation).

**`IbWrapper`** — add order correlators keyed by `orderId`, alongside the existing tick/bar/contract-details sinks:
- `orderStatus` / `openOrder` → complete the **ack awaiter** for that id; drive status. `openOrder`/`openOrderEnd` also feed the reconnect **pushback accumulator**.
- `execDetails` → dedup by `execId` (bounded seen-set); build the fill with **commission = 0**; emit to the order-event lane.
- `commissionAndFeesReport` → (deferred follow-up, open point #2) a late `Portfolio` cash-adjustment keyed by `execId`; **not** joined into the fill at emit (IB delivers it *after* `execDetails`). Plan 4 logs it.
- `error(orderId, code, …)` → fault that order's ack awaiter (`IbRequestException`) **only for genuine reject codes**. IB routes informational warnings (e.g. 399 order-message, 2100-series, 10167) through the same `error(id, code, msg)` with a real `orderId`; a blanket fault would spuriously `Reject` a live order. The handler keeps an explicit **reject-code set** (placement rejects / risk rejects / 10052); non-reject codes are logged and pass through without faulting the awaiter. (Mirrors the existing `IbWrapper.error` special-casing of connectivity codes 1100/1101/1102.)

**`IbOrderGateway`** (the heart, socket-scoped, shared by all per-account clients):
- `Task<long> Place(string account, ResolvedIbContract contract, Order order, CancellationToken ct)` → `NextOrderId()` → build IB `Order{Action, OrderType(MKT/LMT/STP), Tif="DAY", LmtPrice|AuxPrice, Account=account}` → `placeOrder(id, contract, order)` → **await first ack** (bounded timeout) → return the IB order id. Throws `IbRequestException` on reject/timeout.
- `Cancel(long orderId)` → `cancelOrder(orderId, new OrderCancel())`.
- Owns the **order-event lane**: a bounded, generously-sized channel that the pump thread writes via `TryWrite` (never blocks the pump, never drops at IB order rates; overflow → critical log + reconciliation recovers). A single **worker** (off the pump thread) drains it, maps callbacks → `ExecutionReport`, and calls `dispatcher.OnExecutionReport`.
- Subscribes to `IbSession.Reconnected` → exposes the accumulated **account-wide open-order snapshot** (per `order.Account`) and signals the dispatcher to reconcile. The gateway stays **pure transport**: it does not know about sessions or expected orders. The existing `OrderGroupReconciler.DetectAsync`/`CancelOrphansAsync` are reused (not reimplemented); the only new input is the *source* of broker orders — IB's reconnect pushback snapshot rather than `GetOpenOrdersAsync(symbol)`. **The orphan diff is computed by the dispatcher against the UNION of every session's `GetExpectedOrders()` on that `AccountTarget`** — see Error handling #8 (the co-tenancy hazard).

**`IbExchangeOrderClient : IExchangeOrderClient`** (per account, one per `AccountTarget`) — thin adapter: maps `(symbol, side, type, qty, price, stop)` → resolves the contract (Plan-1 `IbContractResolver`) → `gateway.Place(accountName, …)`; returns `ExchangeOrderResult(ibOrderId, [])`. `CancelOrderAsync` → `gateway.Cancel`. `OrderType` mapping: `Market→MKT`, `Limit→LMT`(`LmtPrice`), `Stop→STP`(`AuxPrice`); `Tif="DAY"` always; `Account` always set. **Verified fit:** the empty-`Fills` result flows cleanly through `LiveOrderContext.ProcessOrdersAsync` — the `if (result.Fills.Count > 0)` REST-fill block (`LiveOrderContext.cs:258`) is simply skipped, the order rests under the re-keyed IB id, and fills arrive via the push path. **Zero change to `LiveOrderContext`.**

**`IbAccountFundsSource : IAccountFundsSource`** (new) — wraps `reqAccountSummary(account)` → `{ FreeScaled, QuoteAsset }`, selecting the currency tag from the `executionAsset`. The single paper account name is discovered via `managedAccounts`/`reqAccountSummary`.

**`AccountTargetFactory` (neutralized, was `BinanceAccountTargetFactory`)** — that factory is already venue-neutral except its two injected dependencies (`IAccountFundsSource` + `IExchangeOrderClient`); everything else (Portfolio seed from `DiscoverFunds`, `LiveOrderContext`, `AccountTarget`, `channelCapacity`) is generic. Per the owner's reuse directive, **rename it to a neutral `AccountTargetFactory`** rather than write a parallel IB factory. Because IB needs a **per-account, account-tagged** `IbExchangeOrderClient` over the *shared* gateway (not one injected client), the neutral factory's order-client dependency generalizes to a **per-account provider** (`Func<string, Asset, IExchangeOrderClient>` or equivalent): Binance returns its single client; IB returns `new IbExchangeOrderClient(account, sharedGateway, resolver)`. Funds likewise via the per-account `IAccountFundsSource`. (Binance's funds source + single client are the degenerate 1-account case.)

**`IbMarketDataSource : IMarketDataSource`** (new) — Plan 4 must satisfy the dispatcher's `IMarketDataSource` dependency for IB. `IbSession` is the socket/reconnect owner and exposes no `EnsureSources`; the data plane reaches strategies through the **venue-neutral** `IStrategyDispatch` + `ITickRouter` (Plan 3 wires `IbVenueConnector`/`IbVenueBarSource` into the same `TickRouter`). So `IbMarketDataSource` mirrors `BinanceMarketDataSource` exactly — delegating `Register`/`EnsureSources`/`RecentBars`/`RemoveSources` to `IStrategyDispatch`/`ITickRouter`. (These two seams are venue-agnostic, so the body is identical to Binance's; the type exists to name the IB transport's shared data plane.)

**`IbLiveConnector`** — composition root: one `IbSession` (Plan 3 data plane + `IbOrderGateway`), `OrderRouter` + neutral `AccountTargetFactory` (IB funds source + IB client provider), one `LiveSessionDispatcher`, one `IbMarketDataSource`; wires `gateway → dispatcher.OnExecutionReport`. Registered under `Venue=ib` (Plan 3's host-wiring key) when the host runs as `LiveHost@ib`.

To keep the gateway unit-testable without a socket, an internal `IIbOrderClient` seam (`placeOrder`/`cancelOrder`/`NextOrderId`) fronts the IB transport — mirroring Plan 3's `IIbMarketDataClient`.

## Data flow

### A. Session start — target get-or-create over the shared socket

```
StartLiveSessionCommandHandler → ibConnector.AddSessionAsync(config)
  → dispatcher: target = orderRouter.ResolveTarget(config.AccountName, executionAsset, ct)
       └ new account → AccountTargetFactory.Create(account, executionAsset, ct):
            IbAccountFundsSource.DiscoverFunds(executionAsset) → free funds → seed Portfolio
            client = IbExchangeOrderClient(account) over the shared IbOrderGateway
            (born-running AccountTarget; refcount++)            existing → reuse live target
  → bind strategy ↔ target.OrderContextFor(sessionId)
  → ibMarketDataSource.EnsureSources(reg, instrument ⇒ instrumentScales[instrument])
       (IbMarketDataSource → ITickRouter.EnsureSources; NOT IbSession)
```

One login → N accounts → N targets, all sharing the one `IbOrderGateway`/socket — Plan 2's multi-account seam driven by a real single-socket venue.

### B. Outbound order — strategy → IB (await ack, throw-on-reject)

```
strategy → SessionOrderContext.Submit(order) → LiveOrderContext (account-scoped)
  ProcessOrdersAsync: scale = new ScaleContext(order.Asset)
    result = ibOrderClient.PlaceOrderAsync(order.Asset.Name, side, type, qty, price, stop)
       └ gateway.Place: id = IbConnection.NextOrderId()
            IB Order{Action, OrderType, Tif="DAY", LmtPrice|AuxPrice, Account}
            placeOrder(id, resolvedContract, order); await first ack (bounded)
                 reject / 10052 / timeout ⇒ throw IbRequestException
            return id                                       ◄── ExchangeOrderResult(id, [])
    re-key local→id; OrderMapped ⇒ router.TrackOrder(id, sessionId)
    result.Fills empty ⇒ REST-fill block skipped; order rests under id
```

### C. Inbound fill — IB push → correct target + originating strategy

```
EReader pump thread: execDetails(execId, contract, exec)
  └ IbWrapper: dedup execId; build ExecutionReport (commission if joined, else 0)
       → order-event lane.TryWrite                      (non-blocking; never drops at IB rates)
  gateway worker (OFF pump thread): dispatcher.OnExecutionReport(report)
    → router.TryResolveSession(report.OrderId, out sessionId)   (unmapped ⇒ buffer, replay on TrackOrder)
    → enqueue on the SESSION's EventQueue (exec priority over market data):
         fill = new Fill(id, asset, ts, scale.FromMarketPrice(price), qty, side, commission)
         target.OrderContext.AddFill(fill)        → shared account Portfolio.Apply (under the fill lock)
         originatingStrategy.OnTrade(fill, order) → TradeRegistryModule.OnFill → SL+TP individual orders
```

The fill applies to the account's shared `Portfolio`; `OnTrade` fires on the session that *placed* it. A TP/SL fill flows the identical path → `TradeRegistryModule` cancels the sibling (client-side OCO).

### D. Reconnect + reconciliation — broker is source of truth

```
socket drop → IbWrapper.ConnectionDropped → IbSession reconnect worker (SOLE owner)
  re-establish socket; re-arm NextOrderId from fresh nextValidId; re-subscribe data lanes
  IB pushes back open orders ACCOUNT-WIDE (all symbols, all sessions on the login):
       openOrder(id, contract, order, state)… openOrderEnd
       └ gateway accumulates the working-order snapshot keyed by order.Account
  IbSession.Reconnected → dispatcher reconciles per AccountTarget, reusing the EXISTING
       OrderGroupReconciler.DetectAsync / CancelOrphansAsync (only the broker-orders source
       is new — the pushback snapshot, not GetOpenOrdersAsync(symbol)):
         expected = UNION of every bound session's registry.GetExpectedOrders()   ◄── co-tenancy fix (#8)
         missing-at-broker  ⇒ RepairGroup re-submits        (existing)
         at-broker-not-in-UNION ⇒ CancelOrphansAsync         (existing mechanism)
```

## Error handling (failure modes Plan 4 introduces)

Existing Plan 2 guards carry over unchanged: bounded channels (`EventQueue` `FullMode.Wait`, `MarketDataQueue` `DropNewest`), `IsTrueShutdown(ex, ct)` in long-running loops, OCE filters, buffered-report replay, race-safe target get-or-create (per-account async gate), shared-`Portfolio` fill-lock.

1. **Order-event lane must neither block the pump nor drop a fill.** The EReader pump writes order callbacks; `FullMode.Wait` there would stall all callbacks (incl. data); dropping would lose a money event. → Lane is bounded + generously sized; pump does `TryWrite` only; overflow (essentially impossible at IB order rates) → critical log + reconciliation recovers. The downstream per-session `EventQueue` keeps `FullMode.Wait`, but its writer is the gateway worker (not the pump) — blocking the worker is fine.
2. **Ack lost / order placed-but-timed-out.** `PlaceOrderAsync` throws → the order is marked `Rejected` locally while it may be live at IB. → Accepted divergence; per-account reconnect/periodic reconciliation against the open-order pushback is the authority that repairs it. Documented.
3. **Reject callbacks** (10052 empty-Tif, risk/201). → `Tif="DAY"` on every order kills 10052 by construction; other rejects fault the ack awaiter → throw → `Rejected` → `StartLiveSessionCommandHandler` rollback on the submit path.
4. **`execId` dedup set growth.** → Bounded ring/LRU sized to the reconnect replay window; old ids age out.
5. **Reconnect re-arms the order-id space.** A stale `_nextOrderId` collides with IB's fresh `nextValidId`. → `NextOrderId` re-seeds from the fresh `nextValidId` on each connect (extends the `ResetForReconnect` re-arm).
6. **`order.Account` required on a multi-account login.** An untagged order is ambiguous/rejected under FA. → `IbExchangeOrderClient` always sets `Account`; the sole paper account name is discovered via `managedAccounts`/`reqAccountSummary` (harmless when it's the only account).
7. **Contract-resolution failure on the order path.** → Hard-reject the order — never guess a contract on a money path (data path still drops, Plan 2 failure-mode 5).
8. **Co-tenancy orphan hazard (the load-bearing concurrency risk; this is why Plan 4 is opus-tier).** IB's reconnect `openOrder` pushback is **account-wide** — every symbol, every session on the login. The orphan-cancel *mechanism* already exists and is tested (`OrderGroupReconciler.DetectAsync` computes `OrphanIds`, `CancelOrphansAsync` cancels them, wired in `ReconcileAsync`); Plan 4 does **not** reimplement it. The danger is the **diff input**: `DetectAsync` diffs broker orders against a **single** `registry.GetExpectedOrders()`. If two strategies share one IB account (the co-tenancy the multi-account seam explicitly supports), diffing the account-wide pushback against session A's registry alone flags session B's live SL/TP as orphans → cancels them → strands B's position naked. → **The orphan diff MUST use the UNION of every session's `GetExpectedOrders()` bound to that `AccountTarget`**, computed in the `LiveSessionDispatcher` (which owns `_sessions` per target), never one registry. The `IbOrderGateway` only supplies the account-wide snapshot and stays pure transport. (Binance never hits this: per-symbol `GetOpenOrdersAsync(symbol)` + 1:1 account-to-transport.)

## Testing strategy

Test-First (Principle II): a failing xUnit test before each unit; NSubstitute on internal seams (`InternalsVisibleTo("DynamicProxyGenAssembly2")`). xUnit1051 → every awaited call passes `TestContext.Current.CancellationToken`. The gateway is unit-tested over the internal `IIbOrderClient` seam (no real socket), mirroring Plan 3's `IIbMarketDataClient`.

| # | Unit test | Asserts |
|---|-----------|---------|
| 1 | `NextOrderId` allocation | seeded from `nextValidId`; re-armed on reconnect → no id collision |
| 2 | `IbWrapper` order demux | `orderStatus`→ack; `execDetails`→fill; `commissionReport` joins by `execId`; `error(orderId)`→ack fault |
| 3 | **`execId` dedup** | a re-delivered `execDetails` (reconnect replay) applies the fill once |
| 4 | `IbOrderGateway.Place` | `NextOrderId`→`placeOrder`→await first ack→returns id; reject faults→throws; timeout→throws |
| 5 | **Off-pump handoff** | pump-thread callback only `TryWrite`s; `OnExecutionReport` runs on the worker; a slow handler never blocks the writer |
| 6 | `IbExchangeOrderClient` mapping | `OrderType`→`MKT`/`LMT`/`STP` with `LmtPrice`/`AuxPrice`; `Tif="DAY"`; `Account` set; returns `(id, [])` |
| 7 | **Gross fill at emit** | fill emits on `execDetails` with commission=0; a later `commissionAndFeesReport` does NOT retroactively mutate the emitted fill (deferred cash-adjustment is the follow-up); no `execId` join-at-emit by design |
| 8 | **Multi-account routing (headline)** | two accounts → two targets sharing one gateway/socket; X→A places via A, Y→B via B; A's fill mutates only A's `Portfolio`; report for A's order → X's `OnTrade` not Y's |
| 9 | **Co-tenancy reconciliation (union — the #8 guard)** | two sessions on one `AccountTarget`; account-wide pushback contains BOTH sessions' working orders + one true orphan; reconcile re-submits missing, cancels ONLY the true orphan, and cancels **neither** co-tenant's live orders (orphan diff computed against the union of both registries). Plus the simple case: missing protective → re-submit |
| 10 | **Extraction regression** | the venue-neutral `LiveSessionDispatcher` is unit-tested directly (fake report source). The lift is guarded by the already-green `LiveOrderContextTests` (15), `OrderRouterTests` (5), `MultiAccountRoutingTests` (6) — NOT `BinanceLiveConnectorE2ETests`, which is testnet-gated and skipped in CI |
| 11 | ack/fill race (carried) | fill for an unmapped IB id buffers, replays on `TrackOrder` |

**Gated paper integration** (`[Trait("Category","IbPaper")]`, skipped unless `IB_PAPER_HOST` / `IB_PAPER_PORT` (4004) / `IB_PAPER_CLIENT_ID` set) — the spine's Plan 4 acceptance:
- Market order → **Filled** (position applied, `OnTrade` fired).
- Limit far from market → **Submitted → Cancelled**.
- **TradeRegistry group lifecycle** (entry MKT fills → SL+TP placed as individual orders → one fills → sibling cancelled) — the bracket-lifecycle acceptance via individual orders.
- **Reconciliation** against server-side open-order pushback.
- **Shared single-socket without order-path starvation** (concurrent data sub + orders; fills not delayed by a tick flood).

Multi-account *live* is gated/deferred (paper gateway = one account, per decision 1); test #8 carries the N>1 guarantee at unit level.

## Verification (plan-level "done")

- The order plane plugs into the unchanged `IOrderRouter`/`IAccountTarget` seam, sharing the one `IbSession` socket; collect + execute cohabit one process.
- Order lifecycle against IB paper: market Filled, limit Submitted→Cancelled, TradeRegistry bracket lifecycle.
- Per-account reconciliation against IB's account-wide open-order pushback, **reusing** `OrderGroupReconciler.DetectAsync`/`CancelOrphansAsync`, with the orphan diff computed against the **union** of every co-tenant session's expected orders (repair-missing + cancel-orphan, no co-tenant strand).
- Single-session socket shared without order-path starvation (off-pump handoff; exec-priority preserved).
- `LiveSessionDispatcher` extraction green: direct dispatcher unit tests + the already-green `LiveOrderContextTests`/`OrderRouterTests`/`MultiAccountRoutingTests` unchanged in behavior; both connectors compose the neutral core.
- Full LiveHost.Application + Infrastructure + WebApi suites green; Domain untouched (zero IB vocab, zero new ProjectRefs); build 0/0.

## Open points (deferred, flagged)

1. **Native IB OCA (server-side one-cancels-all)** — would close the client-side-OCO double-fill window and survive client death, but is an IB-specific divergence from the venue-neutral `TradeRegistryModule` (the same window already exists on Binance and is accepted). Deferred as a cross-venue robustness enhancement.
2. **Commission accuracy** — commission is **0 at fill emit** (IB delivers `commissionAndFeesReport` after `execDetails`); it does not affect `TradeRegistryModule` gross PnL, only `Portfolio` cash. The follow-up is a precise **deferred `Portfolio` cash-adjustment** applied when the report lands (keyed by `execId`); not built in Plan 4.
3. **Ack-timeout divergence** (Error handling #2) — an order placed but un-acked is marked `Rejected` locally and repaired only by the next reconcile; the divergence window is documented, not eliminated.
4. **Multi-account / multi-currency live** — the live E2E runs a single USD paper account; FA sub-account and non-USD math press on the carried-forward money-model TODO (the ledger is a unit-less `long`; co-tenancy is fenced on tick+quote). Out of scope until the units-bearing Money model lands.
5. **`reqExecutions` on reconnect** — if reconciliation pulls historical executions (not just open orders), `execId` dedup (test #3) is the guard against double-applying; the decision to call `reqExecutions` vs rely on open-order pushback only is a writing-plans detail.
