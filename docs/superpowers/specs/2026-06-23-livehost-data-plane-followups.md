# LiveHost Data Plane (Plan 4) — Follow-ups & Pre-Phase-5/6 Refactoring Scope

**Date:** 2026-06-23
**Status:** Scope/backlog. Each item below gets its own brainstorm → writing-plans → subagent-driven-development cycle when picked up. Nothing here is implemented yet.
**Gating:** The **Subscription-model unification (§A)** and **bar capability split (§B)** SHOULD be addressed before the next decomposition phases — **Plan 5** (account-scoped `IOrderRouter` / multi-account) and **Plan 6** (`collection.json` roles + M6 live alt-bars) — because both later plans build on the subscription/strategy contracts these items reshape. The OCE-filter fix (§D) is independent and high-priority (money host).

Emerged from the as-built review of Plan 4 (`feat/livehost-data-plane`). Plan 4 deliberately stopped at the data plane; the items below are the simplification/unification debt it surfaced. Owner directive recorded: *"break strategies freely, they are garbage"* — this lifts the *strategy-body* rewrite cost but NOT the *core-engine* blast radius (see §A).

---

## §A — Subscription-model unification (MAJOR; precedes Plan 5/6)

**Problem.** Two parallel subscription representations exist, paired positionally:
- `DataSubscription(Asset Asset, TimeFrame TimeFrame, string FeedKey, bool IsExportable)` — *resolved* (concrete `Asset`), strategy/engine-facing. Its flat `TimeFrame` is **dated**: an alt-bar or tick subscription has no meaningful `TimeFrame`.
- `DataFeedSubscription` (abstract; `TimeBarSubscription`/`AltBarSubscription`/`TickSubscription`) — *typed kind*, asset-by-name, API/wire-facing. Correctly models the kind, but lacks the resolved `Asset`.

`LiveSessionConfig` carries **both** (`Subscriptions` + `RawSubscriptions`), 1:1 positional, guarded by `SessionInterest.Build`'s length check — a fragility, not a type-level invariant.

**Target.** One subscription model: the typed `DataFeedSubscription` hierarchy made *resolved* by carrying the `Asset` — e.g. a `ResolvedSubscription { DataFeedSubscription Spec; Asset Asset }` wrapper, or `Asset` attached to the resolved hierarchy. Retire flat `DataSubscription` + its `TimeFrame`. Collapse `LiveSessionConfig`'s dual-list to one. Replace the positional pairing with a structural one.

**Blast radius (~73 src files; mostly CORE infra, NOT strategy bodies):**
- `BacktestEngine.EmitBar(…, DataSubscription)` — engine data flow.
- `MarketDataSnapshot` — uses `DataSubscription` as its **dictionary KEY** (`_data[subscription]`); must be re-keyed (equality semantics).
- `IIndicatorFactory.Create(…, DataSubscription)`, `EmittingIndicatorDecorator/Factory`.
- Strategy callback signatures: `OnBarComplete(Int64Bar, DataSubscription)`, `OnTradeTick(in TradeTick, DataSubscription)`; every module (`IFilterModule.Initialize`, CrossAsset, Regime).
- Backtest/optimization/validation/persistence (`BacktestPreparer`, `OptimizationSetupHelper`, `StrategySubscriptionFactory`, run records).
- Existing resolver seam to build on: `StrategySubscriptionFactory.FromPrimary(DataFeedSubscription, Asset) → DataSubscription`.

> "Break strategies freely" does not shrink this: the heavy consumers are the engine, the feed-snapshot key, indicators, and optimization — core infra, not the (disposable) strategy implementations.

**Acceptance sketch:** one subscription type end-to-end; `MarketDataSnapshot` re-keyed; `LiveSessionConfig` single list; full suite green; backtest golden behavior unchanged.

## §A′ — `LiveSessionConfig.PrimaryAsset` → primary/trade-subscription marker

`PrimaryAsset` is **not** redundant — it's the execution/denomination asset (orders, cancel-all, 3-phase reconciliation `CancelOrphansAsync(PrimaryAsset.Name)`, `ScaleContext`, portfolio init all key off it), distinct from data-only feeds (CrossAsset/Pairs context the session does not trade). Under §A, model it as *which subscription is the trade target* rather than a duplicated `Asset` field. Folds into the §A effort.

## §B — Bar capability split (completes routing→capability; precedes Plan 5/6)

Plan 4 made live routing capability-driven and **deleted `LiveEventRouting` entirely** (bars → `IInt64BarStrategy`, fills → every `IStrategy`, trade-ticks → `ITradeTickStrategy`, `OnBarStart` → venue sources that support it). Remaining debt: `IInt64BarStrategy` still conflates "is a strategy" with "consumes bars," and `OnBarStart` is a defaulted method (not expressible as `is X`).

**Target.** Split into capability interfaces: `IBarStrategy.OnBarComplete` + an `OnBarStart` capability (e.g. `IBarStartStrategy` or an opt-in), alongside `ITradeTickStrategy` (done) and `IQuoteTickStrategy` (§C). Strategies implement only what they consume; dispatch fans by capability.
**Note:** backtest still calls `OnBarStart`/`OnBarComplete` directly (`BacktestEngine`) — that path is unaffected and must stay working.

## §C — `IQuoteTickStrategy.OnQuoteTick` (deferred until a quote strategy exists — YAGNI)

`QuoteTick` is BBO *state* (bid/ask/sizes, no executed volume) — it does NOT aggregate to bars / `SourceRecord` (only trades + candles do). When a quote-driven strategy first appears, add `IQuoteTickStrategy.OnQuoteTick(in QuoteTick, DataSubscription)` mirroring `ITradeTickStrategy`, plus a `QuoteEvent` dispatch tap (currently `QuoteEvent` is archival-only). No work until the first consumer.

## §D — Pre-existing OCE-filter in reconciliation loop (HIGH priority; independent; own branch)

`BinanceLiveConnector` reconciliation **timer loop** (~`:505`, Plan-3-era, NOT introduced by Plan 4) uses `catch when (ex is not OperationCanceledException)`. An `HttpClient` `TaskCanceledException` **is** an `OperationCanceledException`, so it escapes the filter, is caught by the outer OCE handler, and the reconciliation loop can **silently die** for the connector's lifetime while `ct` is live — on the money host. Fix per `[[feedback_oce_filter_pattern]]` (`IsTrueShutdown(ex, ct)` or `when (ct.IsCancellationRequested)`), with a focused test. Independent of §A–§C — can land anytime; should be soon.

## §E — Cosmetic cleanups (low priority; bundle into any of the above)

- Unused `_logger` fields in `TickRouter` / `StrategyDispatch`.
- `Program.cs` fully-qualified DI type names (style consistency).
- Double `entry.Subscriptions.ToList()` in `GetSessionSnapshotAsync` (low-frequency path).
- UTF-8 BOM accidentally added to ~10 relocated engine test files during the Domain.Aggregation move.
- Missing unit test for the tick-sub-at-index-0 flat-`Bars` fallback in `SessionSnapshotBars`.

---

## Suggested sequencing

1. **§D** (OCE-filter) — anytime, soon; own small branch + test.
2. **§A + §A′ + §B** — the unification/capability effort (likely the bulk of "Strategy Framework v2"); brainstorm → plan → SDD; lands before Plan 5/6. §E folds in opportunistically.
3. **Plan 5** (account-scoped `IOrderRouter` / multi-account) and **Plan 6** (`collection.json` roles + M6 live alt-bars: threshold-freeze is DONE, partial-bar seeding remains) — after the subscription/strategy contracts are unified.
4. **§C** — only when a quote-driven strategy is actually needed.

Cross-reference: memory `[[project_strategy_framework_v2]]` (deferred items), `[[project_service_decomposition]]` (Plan 4 complete; Plans 5/6 next).
