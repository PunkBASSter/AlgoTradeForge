# §A + §A′ + §B — Subscription-model unification + bar capability split — Design

**Date:** 2026-06-23
**Scope items:** §A, §A′, §B of `docs/superpowers/specs/2026-06-23-livehost-data-plane-followups.md`
**Branch:** committed directly on `feat/livehost-data-plane` (Plan 4 unmerged; owner directive — stacks on §D)
**Status:** Design approved; pending writing-plans → subagent-driven-development.
**Precedes:** Plan 5 (account-scoped `IOrderRouter`), Plan 6 (`collection.json` roles + M6 live alt-bars).

## Problem

Two parallel subscription representations exist, paired positionally:

- **`DataSubscription(Asset, TimeFrame, FeedKey, IsExportable)`** (`src/AlgoTradeForge.Domain/Strategy/DataSubscription.cs:4`) — the *resolved* runtime type carrying a concrete `Asset`. It is the workhorse: ~73 src files, every engine/strategy/indicator/persistence/optimization callback. Its flat `TimeFrame` is dated — an alt-bar or tick subscription has no meaningful `TimeFrame` (it carries a placeholder/sentinel).
- **`DataFeedSubscription`** (abstract; `TimeBarSubscription`/`AltBarSubscription`/`TickSubscription`/`SideFeedSubscription` — `src/AlgoTradeForge.Domain/Strategy/Subscriptions/`) — the *typed kind*, asset-by-name, JSON wire/API/persistence facing. Correctly models the kind but lacks the resolved `Asset`.

`LiveSessionConfig` (`src/AlgoTradeForge.Domain/Live/LiveSessionConfig.cs:10–15`) carries **both** lists (`Subscriptions` + `RawSubscriptions`), 1:1 positional, guarded at runtime by length-checks in `SessionInterest.Build` (`…/Live/DataPlane/SessionInterest.cs:31`) and `SessionSnapshotBars.Build` (`…/Live/DataPlane/SessionSnapshotBars.cs:21`) — a fragility, not a type-level invariant. Plus a separately-stored `PrimaryAsset` field.

### Findings from the as-built exploration (these reshape the handover's stated blast radius)

1. **`MarketDataSnapshot` is dead in production.** It appears only in its own file (`src/AlgoTradeForge.Domain/History/MarketDataSnapshot.cs`) and its own test (`tests/AlgoTradeForge.Domain.Tests/History/MarketDataSnapshotTests.cs`). Zero production constructors/readers; nothing in the Private repo. The engine uses parallel arrays (`RunState.Series[]` + `RunState.Subscriptions[]`), never the snapshot. The handover's "re-key MarketDataSnapshot (the hard part)" is a **deletion**.
2. **Two divergent resolvers.** Backtest/optimization use `StrategySubscriptionFactory.FromPrimary` (`src/AlgoTradeForge.Application/Backtests/StrategySubscriptionFactory.cs:16`); the live path has its *own* inline switch in `StartLiveSessionCommandHandler` (`src/AlgoTradeForge.LiveHost.Application/Live/StartLiveSessionCommandHandler.cs:33–40`) that disagrees in three places: AltBar `TimeFrame` (`ResolveSourceTimeFrame(feedId)` vs `default`), Tick `FeedKey` (`"ticks"` vs `"tick"`), SideFeed handling (meaningful throw vs fall-through). A latent live/backtest inconsistency.
3. **`DataFeedRole.Primary/Side` exists but is unused in the live path.** The live handler blindly takes `resolvedSubscriptions[0].Asset` as primary. `DataFeedRole` is only honored in backtest/optimization.
4. **`IsExportable` lives only on `DataSubscription`** (event-bus gating; mutated in the debug path via `sub with { IsExportable = true }` — `StartDebugSessionCommandHandler.cs:71`). It needs an explicit home in the unified model.
5. **§B is mostly a live-dispatch concern.** `IInt64BarStrategy.OnBarStart` is a default-interface-method; `StrategyBase` makes both `OnBarStart`/`OnBarComplete` concrete and `BacktestEngine` calls them directly. The capability split only changes how *live* `StrategyDispatch` fans out.

## Decisions (approved)

- **Resolved type:** attach the resolved `Asset` onto the `DataFeedSubscription` hierarchy; retire flat `DataSubscription`.
- **MarketDataSnapshot:** delete it + its test.
- **PrimaryAsset → `ExecutionAsset`:** rename the (asset-vs-subscription-conflating) `PrimaryAsset` to an execution-explicit `ExecutionAsset`, derived from the trade-target subscription (`Role == Primary`, else index-0 fallback); drop the stored field. `DataFeedRole.Primary` is retained as the *backtest clock* distinction. Multiple execution targets / multiple backtest clocks are deferred to Plan 5/6 (live already multi-triggers).
- **IsExportable home (sub-decision A):** keep on the record as a `[JsonIgnore] bool`.
- **§B shape (sub-decision B):** a separate `IBarStartStrategy` opt-in interface.
- **Migration (sub-decision C):** direct retire of `DataSubscription` — no temporary bridge/alias.

## Target design

### 1. Unified type — `DataFeedSubscription` carries the resolved data

```csharp
public abstract record DataFeedSubscription(string AssetName, string Exchange, DataFeedRole Role)
{
    [JsonIgnore] public Asset? Asset { get; init; }        // resolved server-side; non-null once resolved
    [JsonIgnore] public bool IsExportable { get; init; }   // event-bus gating; set in debug path via `with`
}
```

- `[JsonIgnore]` keeps the JSON wire/persistence contract byte-identical: inbound commands still carry `AssetName` (string); persistence (`BacktestRunRecord`/`OptimizationRunRecord` already store `IReadOnlyList<DataFeedSubscription>`) still serializes only `AssetName`/`Exchange`/`Role`/kind-fields. `Asset` is populated by the single resolver before the engine/strategy sees it.
- **Contract:** every callback receives a *resolved* subscription (`Asset` non-null). Provide `Asset RequireAsset()` (throws if accessed unresolved) for call sites that want a non-null guarantee, and a derived **`FeedKey()`** extension (`TimeBar → "ohlcv"`, `AltBar → FeedId`, `Tick → "ticks"`) replacing the flat `FeedKey` field.
- The flat type's `TimeFrame` is absorbed: `TimeBarSubscription.TimeFrame` already exists; consumers that read a timeframe for alt-bar/tick (`BacktestEngine.EmitBar` formatting, `HistoryRepository.Load`, `ParameterKeyBuilder`) switch on subtype instead of reading a placeholder.

### 2. One resolver

Collapse `StrategySubscriptionFactory.FromPrimary` and the inline switch in `StartLiveSessionCommandHandler` into a single resolver: `DataFeedSubscription Resolve(DataFeedSubscription spec, Asset asset) => spec with { Asset = asset }`. Both the backtest preparer (`BacktestPreparer.cs:77`), optimization (`OptimizationSetupHelper.cs:152`), and the live handler call it. The three divergences vanish.

### 3. §A′ — `ExecutionAsset` (not `PrimaryAsset`), derived from the trade-target subscription

`LiveSessionConfig` collapses to a single `IReadOnlyList<DataFeedSubscription>` (resolved). Drop the stored `PrimaryAsset` field and the `Subscriptions`+`RawSubscriptions` dual list.

**Two concepts were conflated in `PrimaryAsset`** (they coincide only under the Plan-4 single-asset-per-session constraint):
- the **backtest clock** — `DataFeedRole.Primary` is the subscription that drives `BacktestEngine`'s single bar-advance loop; `Side` feeds are not triggers, they're queried on demand via `IFeedContext`. This stays.
- the **execution / denomination asset** — what orders, `ScaleContext`, portfolio, and reconciliation key off. This is an *execution* concept, not a data role.

`DataFeedRole.Primary/Side` is retained as the backtest-clock distinction. The execution asset becomes an execution-explicit derived value, **`ExecutionAsset`**, with a `Role == Primary` selection and an **index-0 fallback**:
```csharp
Asset ExecutionAsset => (subscriptions.FirstOrDefault(s => s.Role == DataFeedRole.Primary)
                         ?? subscriptions[0]).RequireAsset();
```
The live path begins honoring `DataFeedRole` (today it ignores it and blindly takes index 0). Consumers renamed from the primary-asset field to `ExecutionAsset`: balance validation + `ScaleContext` (`BinanceLiveConnector.cs:251,262,329`), `LiveOrderContext` order submission (`LiveOrderContext.cs:225`), portfolio init (`BinanceLiveConnector.cs:281`), fill parsing (`BinanceLiveConnector.cs:593`), cancel-all (`:417`), 3-phase reconciliation `DetectAsync`/`CancelOrphansAsync` (`:500,518`). `LiveSessionEntry.PrimaryAsset` → `LiveSessionEntry.ExecutionAsset`, derived once at registration. The `SessionInterest`/`SessionSnapshotBars` length-guards are deleted — the positional invariant no longer exists.

**Deliberately deferred (not §A′):** multiple execution targets is Plan 5 (account-scoped `IOrderRouter`); multiple simultaneous backtest clocks is a deeper engine change (Plan 6+). Live *already* fans events from every bar/tick subscription via `StrategyDispatch`, so multi-trigger listening (e.g. arbitrage on several tick feeds) needs no new work — what is single today is only the execution asset and the backtest clock. Keeping role-on-subscription + asset-derived means neither needs undoing when Plan 5/6 extends to multiple.

### 4. §B — bar capability split

```csharp
public interface IBarStrategy : IStrategy { void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription); }
public interface IBarStartStrategy : IStrategy { void OnBarStart(Int64Bar bar, DataFeedSubscription subscription); }
```

- `IInt64BarStrategy` is replaced by `IBarStrategy` (rename + drop the defaulted `OnBarStart`). `StrategyBase` implements **both** `IBarStrategy` and `IBarStartStrategy`, retaining the existing `OnBarStart` timestamp-capture machinery → `OnBarStartInner` virtual hook. This is a deliberate **behavior-preserving** choice for the engine-critical path: every `StrategyBase`-derived strategy keeps receiving `OnBarStart` exactly as today. The capability split's segregation benefit therefore lands at the *interface contract* (a strategy can implement `IBarStrategy` alone) and *live dispatch* (fans by `is IBarStartStrategy`), not in forcing `StrategyBase` strategies to opt in. (Tightening `StrategyBase` to drop `IBarStartStrategy` so bar-start becomes a true rare opt-in is a possible future change but is OUT OF SCOPE here — it would alter the mid-bar `_currentBarTimestamp` capture window and break backtest-identical behavior.)
- `BacktestEngine` calls `OnBarComplete` directly; `OnBarStart` only when `strategy is IBarStartStrategy` (behavior-identical — `StrategyBase` implements it, so the call still happens). Live `StrategyDispatch` fans `OnBarStart` only to `is IBarStartStrategy` implementers, replacing the defaulted-method dispatch.
- `LiveSessionConfig.Strategy`, `SessionInterest.Strategy`, `LiveSessionRegistration.Strategy` retype `IInt64BarStrategy` → `IBarStrategy`.

### 5. `MarketDataSnapshot` — deleted

Delete `src/AlgoTradeForge.Domain/History/MarketDataSnapshot.cs` and `tests/AlgoTradeForge.Domain.Tests/History/MarketDataSnapshotTests.cs`.

### 6. Callback/consumer signature changes (`DataSubscription` → `DataFeedSubscription`)

Direct retire, no bridge. Every signature flips type:
- **Strategy callbacks:** `IBarStrategy.OnBarComplete`, `IBarStartStrategy.OnBarStart`, `ITradeTickStrategy.OnTradeTick`; `StrategyBase.OnBar*`/`OnBar*Inner`; `ModularStrategyBase.OnBar*Inner`/`OnContextUpdated`/`EvaluateEntry`; `StrategyContextBase.Update`/`CurrentSubscription`; `IStrategy.DataSubscriptions`/`StrategyParamsBase.DataSubscriptions` list element type.
- **Engine/indicators:** `BacktestEngine` `OnBarStart`/`OnBarComplete`/`EmitBar` call sites + `RunState.Subscriptions`; `IIndicatorFactory.Create`, `EmittingIndicatorFactory`/`EmittingIndicatorDecorator`, `PassthroughIndicatorFactory`.
- **Modules:** `IFilterModule.Initialize`; `AtrVolatilityFilterModule`, `RegimeFilterModule`, `RegimeDetectorModule`, `CrossAssetModule.Initialize(2 subs)`/`Update`, `ArimaForecastFilterModule` (Private).
- **Application/optimization/persistence:** `IHistoryRepository.Load(DataSubscription…)` legacy overload (collapse onto the typed overload), `BacktestPreparer`, `OptimizationSetupHelper`/`OptimizationTaskExecutor`/`BoundedTrialQueue`/`ParameterKeyBuilder` (the `List<DataSubscription>` cases → `List<DataFeedSubscription>`).
- **LiveHost:** `LiveSessionConfig`, `LiveSessionRegistration`, `BarInterest`, `TickInterest`, `SessionInterest`, `SessionSnapshotBars`, `StrategyDispatch`, `StartLiveSessionCommandHandler`.
- **Private repo:** all 4 `CreateSwingDetector(DataSubscription)` + `PivotTrendBreakoutStrategyBase.OnBarCompleteInner` + Pairs `ReferenceEquals(sub, DataSubscriptions[0])` routing.
- **Tests:** ~74 main-repo + 6 Private test files; "break strategies freely" applies to strategy bodies/tests.

### 7. Equality / routing

`DataFeedSubscription` is a record → value equality now spans `(AssetName, Exchange, Role, kind-fields, [JsonIgnore] Asset, [JsonIgnore] IsExportable)`. Routing that relied on `DataSubscription` value/reference equality stays correct because the engine/live dispatch deliver the *same resolved instance* held in `strategy.DataSubscriptions`: `ModularStrategyBase.IndexOf(subscription)` (value eq), `CrossAssetModule sub == _sub1` (value eq), `PairsTradingStrategy ReferenceEquals(sub, DataSubscriptions[0])` (reference eq) all hold. The previously-noted `IsExportable`-in-key hazard is moot once `MarketDataSnapshot` is deleted.

## Testing & guards

- **Backtest behavior-identical:** §A touches the core engine, so the existing engine/golden tests are the safety net. A backtest-equivalence check (golden run unchanged before/after) gates the engine-touching tasks.
- **Private solution green:** Private strategies (`ZigZag*`, `AtrZigZag*`, Pairs) + their tests build against these signatures — `../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx` must build and `dotnet test` green.
- **Single resolver behavior:** a test pins that the unified resolver produces the (previously divergent) values consistently for AltBar/Tick/Side across both the backtest and live entry points.
- **§A′:** tests that `ExecutionAsset` selection drives the execution/denomination asset (orders/scale/reconciliation): (a) `Role == Primary` chosen when present, including a config where the primary is not at index 0; (b) the **index-0 fallback** when no subscription has `Role == Primary`.
- **§B:** dispatch tests that `OnBarStart` reaches only `IBarStartStrategy` implementers and `OnBarComplete` reaches all `IBarStrategy`.
- **Perf/alloc:** if the callback parameter-type change touches the engine hot path, run the BenchmarkDotNet harness (`run-benchmarks`) and compare Mean + Allocated against the pre-change baseline.
- **Conventions:** Int64 money (`MoneyConvert.ToLong`/`ScaleContext`), no `Async` suffix (`[[feedback_no_async_suffix]]`), one-type-per-file, Domain zero ProjectReferences, no backward-compat shims, nothing left dead.

## Acceptance

- One subscription type end-to-end (`DataFeedSubscription`, resolved via `[JsonIgnore] Asset`); flat `DataSubscription` deleted; no compatibility shim remains.
- `LiveSessionConfig` carries a single resolved list; the `PrimaryAsset` field is replaced by a derived `ExecutionAsset` (`Role == Primary`, index-0 fallback); `SessionInterest`/`SessionSnapshotBars` positional length-guards gone.
- One resolver; the three live/backtest divergences eliminated.
- `IBarStrategy` + `IBarStartStrategy`; `IInt64BarStrategy` retired; live dispatch fans `OnBarStart` by capability; backtest behavior identical.
- `MarketDataSnapshot` + its test deleted.
- `dotnet build AlgoTradeForge.slnx` + full test suite green; `../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx` build + tests green; backtest golden unchanged; benchmarks within noise (or justified).

## Out of scope (deferred)

- §C `IQuoteTickStrategy.OnQuoteTick` — YAGNI until a quote-driven strategy exists.
- Plan 5 (account-scoped `IOrderRouter`) and Plan 6 (`collection.json` roles + M6 live alt-bars) — build on this unified contract, come after.
- §E cosmetic cleanups (unused `_logger` in `TickRouter`/`StrategyDispatch`, `Program.cs` FQ DI names, double `ToList` in `GetSessionSnapshotAsync`, UTF-8 BOM on relocated engine test files) — fold in opportunistically during the migration tasks where the file is already open.

## Cross-references

- Parent scope: `docs/superpowers/specs/2026-06-23-livehost-data-plane-followups.md` §A/§A′/§B.
- `[[project_strategy_framework_v2]]`, `[[project_service_decomposition]]`, `[[feedback_no_async_suffix]]`.
- §D (just landed): `docs/superpowers/specs/2026-06-23-livehost-reconciliation-oce-filter-design.md`.
