# §A + §A′ — Subscription-model unification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retire the flat `DataSubscription` onto the resolved `DataFeedSubscription` (Asset attached via `[JsonIgnore]`), collapse `LiveSessionConfig`'s dual list, derive `ExecutionAsset` from the `Role==Primary` subscription, unify the two resolvers, and delete dead `MarketDataSnapshot`.

**Architecture:** `DataFeedSubscription` (the typed wire/persistence hierarchy) gains a runtime-only resolved `Asset` and `IsExportable` (`[JsonIgnore]`), plus `RequireAsset()`/`FeedKey()` accessors. A single `Resolve(spec, asset)` replaces the divergent backtest/live resolvers. The flat `DataSubscription` is then substituted by `DataFeedSubscription` across the whole public solution in one atomic, mostly-mechanical change (type-system-forced — no bridge), guarded by the existing engine/golden test suite. The Private solution (separate build) flips next; benchmarks confirm the engine hot path.

**Tech Stack:** C# 14 / .NET 10, xUnit, NSubstitute, `System.Text.Json` (polymorphic), BenchmarkDotNet. No new packages.

**Spec:** `docs/superpowers/specs/2026-06-23-livehost-subscription-unification-design.md`

## Global Constraints

- **One `dotnet` process at a time.** Build/test strictly sequential. Use `powershell.exe`, never `pwsh`.
- **No backward-compat shim / bridge / alias for `DataSubscription`.** It is deleted by the end; nothing vestigial remains.
- **`[JsonIgnore]` on the resolved `Asset` and `IsExportable`** — the JSON wire/persistence contract for `DataFeedSubscription` must stay byte-identical (still serializes `AssetName`/`Exchange`/`Role`/kind-fields only).
- **`DataFeedRole.Primary` stays meaning the backtest clock.** `ExecutionAsset = (subs.FirstOrDefault(s => s.Role == DataFeedRole.Primary) ?? subs[0]).RequireAsset()`.
- **`IInt64BarStrategy` is left as-is** in name and shape (both `OnBarStart` defaulted + `OnBarComplete` required) — only its subscription parameter retypes. No `IBarStrategy`/`IBarStartStrategy`.
- **Backtest behavior identical** — the existing Domain/Application engine + strategy test suites are the golden guard; they must pass unchanged.
- **Int64 money convention** (`MoneyConvert.ToLong` / `ScaleContext`); **no `Async` suffix** on new/changed async methods; **one type per file**; **Domain has zero ProjectReferences**.
- **Private solution must stay green:** `../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx` build + `dotnet test` after Task 3.
- **Implementers must NOT commit** — the controller stages + commits after verifying each task's diff (hook denies subagent `git add`).

---

### Task 1: Additive prep on `DataFeedSubscription` (resolved Asset + accessors + single resolver)

Purely additive — `DataSubscription` is untouched and still used; the solution stays green. This lands the new API that Task 2 will substitute onto.

**Files:**
- Modify: `src/AlgoTradeForge.Domain/Strategy/Subscriptions/DataFeedSubscription.cs` (add resolved members)
- Modify: `src/AlgoTradeForge.Domain/Strategy/Subscriptions/DataFeedSubscriptionExtensions.cs` (add `FeedKey()`, `RequireAsset()`, `ResolveExecutionAsset()`)
- Create: `src/AlgoTradeForge.Domain/Strategy/Subscriptions/SubscriptionResolver.cs` (the single resolver)
- Test: `tests/AlgoTradeForge.Domain.Tests/Strategy/Subscriptions/SubscriptionResolverTests.cs` (new)

**Interfaces:**
- Produces:
  - `DataFeedSubscription` gains `[JsonIgnore] public Asset? Asset { get; init; }` and `[JsonIgnore] public bool IsExportable { get; init; }`.
  - `DataFeedSubscriptionExtensions.RequireAsset(this DataFeedSubscription) : Asset` (throws if `Asset` null).
  - `DataFeedSubscriptionExtensions.FeedKey(this DataFeedSubscription) : string` (`TimeBar → "ohlcv"`, `AltBar → FeedId`, `Tick → "ticks"`, `Side → FeedId`).
  - `DataFeedSubscriptionExtensions.ResolveExecutionAsset(this IReadOnlyList<DataFeedSubscription>) : Asset` (`FirstOrDefault(Role==Primary) ?? [0]`, then `RequireAsset()`).
  - `SubscriptionResolver.Resolve(DataFeedSubscription spec, Asset asset) : DataFeedSubscription` (`spec with { Asset = asset }`).
- Consumes: `Asset` (`AlgoTradeForge.Domain`), `DataFeedRole`, the four subtypes.

- [ ] **Step 1: Write the failing tests**

Create `tests/AlgoTradeForge.Domain.Tests/Strategy/Subscriptions/SubscriptionResolverTests.cs`. Use an existing test asset helper — search the test project for a `CryptoAsset`/`Asset` factory already used by sibling tests (e.g. an `AssetTestFactory` or inline `CryptoAsset.Create(...)`); mirror that construction. The asset only needs `Name`/`Exchange`.

```csharp
using System.Collections.Generic;
using AlgoTradeForge.Domain.Assets;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Subscriptions;

public class SubscriptionResolverTests
{
    private static Asset Btc() => CryptoAsset.Create("BTCUSDT", "binance"); // match sibling tests' factory if different
    private static Asset Eth() => CryptoAsset.Create("ETHUSDT", "binance");

    [Fact]
    public void Resolve_AttachesAsset_WithoutMutatingWireFields()
    {
        var spec = new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        var resolved = SubscriptionResolver.Resolve(spec, Btc());

        Assert.Equal("BTCUSDT", resolved.AssetName);
        Assert.Equal(DataFeedRole.Primary, resolved.Role);
        Assert.Same(Btc().GetType(), resolved.RequireAsset().GetType());
        Assert.Equal("BTCUSDT", resolved.RequireAsset().Name);
    }

    [Fact]
    public void RequireAsset_Throws_WhenUnresolved()
    {
        var spec = new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"));
        Assert.Throws<System.InvalidOperationException>(() => spec.RequireAsset());
    }

    [Theory]
    [InlineData("TimeBar", "ohlcv")]
    [InlineData("Tick", "ticks")]
    public void FeedKey_DerivesFromKind(string kind, string expected)
    {
        DataFeedSubscription spec = kind == "TimeBar"
            ? new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))
            : new TickSubscription("BTCUSDT", "binance", DataFeedRole.Primary);
        Assert.Equal(expected, spec.FeedKey());
    }

    [Fact]
    public void FeedKey_AltBar_IsFeedId()
    {
        var spec = new AltBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, "EqV_1m_500");
        Assert.Equal("EqV_1m_500", spec.FeedKey());
    }

    [Fact]
    public void ResolveExecutionAsset_PrefersPrimary_EvenWhenNotIndex0()
    {
        var subs = new List<DataFeedSubscription>
        {
            SubscriptionResolver.Resolve(new TimeBarSubscription("ETHUSDT", "binance", DataFeedRole.Side, TimeFrame.Parse("1h")), Eth()),
            SubscriptionResolver.Resolve(new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")), Btc()),
        };
        Assert.Equal("BTCUSDT", subs.ResolveExecutionAsset().Name);
    }

    [Fact]
    public void ResolveExecutionAsset_FallsBackToIndex0_WhenNoPrimary()
    {
        var subs = new List<DataFeedSubscription>
        {
            SubscriptionResolver.Resolve(new TimeBarSubscription("ETHUSDT", "binance", DataFeedRole.Side, TimeFrame.Parse("1h")), Eth()),
            SubscriptionResolver.Resolve(new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Side, TimeFrame.Parse("1h")), Btc()),
        };
        Assert.Equal("ETHUSDT", subs.ResolveExecutionAsset().Name);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `powershell.exe -NoProfile -Command "dotnet build AlgoTradeForge.slnx"`
Expected: compile errors — `Asset`/`RequireAsset`/`FeedKey`/`ResolveExecutionAsset`/`SubscriptionResolver` not defined. (First confirm the test asset factory name compiles; fix the `Btc()`/`Eth()` helper to match the sibling tests' real factory if `CryptoAsset.Create` differs.)

- [ ] **Step 3: Add the resolved members to `DataFeedSubscription`**

In `DataFeedSubscription.cs`, add to the abstract base body (keep the `[JsonPolymorphic]`/`[JsonDerivedType]` attributes as-is) — add `using System.Text.Json.Serialization;`:

```csharp
public abstract record DataFeedSubscription(string AssetName, string Exchange, DataFeedRole Role)
{
    [JsonIgnore] public Asset? Asset { get; init; }
    [JsonIgnore] public bool IsExportable { get; init; }
}
```
(Add `using AlgoTradeForge.Domain.Assets;` if `Asset` isn't already in scope — note `Asset` base type lives in `AlgoTradeForge.Domain`.)

- [ ] **Step 4: Add the accessors to `DataFeedSubscriptionExtensions`**

```csharp
public static Asset RequireAsset(this DataFeedSubscription sub) =>
    sub.Asset ?? throw new InvalidOperationException(
        $"DataFeedSubscription for '{sub.AssetName}' is unresolved (Asset is null).");

public static string FeedKey(this DataFeedSubscription sub) => sub switch
{
    TimeBarSubscription => "ohlcv",
    AltBarSubscription ab => ab.FeedId,
    TickSubscription => "ticks",
    SideFeedSubscription sf => sf.FeedId,
    _ => throw new ArgumentOutOfRangeException(nameof(sub), sub.GetType().Name, "Unknown subscription kind"),
};

public static Asset ResolveExecutionAsset(this IReadOnlyList<DataFeedSubscription> subs) =>
    (subs.FirstOrDefault(s => s.Role == DataFeedRole.Primary) ?? subs[0]).RequireAsset();
```
(Ensure `using System.Linq;` and `using System.Collections.Generic;`.)

- [ ] **Step 5: Add the single resolver**

Create `src/AlgoTradeForge.Domain/Strategy/Subscriptions/SubscriptionResolver.cs`:

```csharp
namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

public static class SubscriptionResolver
{
    public static DataFeedSubscription Resolve(DataFeedSubscription spec, Asset asset) =>
        spec with { Asset = asset };
}
```
(Add `using AlgoTradeForge.Domain.Assets;` if needed for `Asset`.)

- [ ] **Step 6: Build + run the new tests**

Run: `powershell.exe -NoProfile -Command "dotnet test tests/AlgoTradeForge.Domain.Tests/ --filter SubscriptionResolverTests"`
Expected: PASS (all cases). Then a full solution build to confirm nothing else broke:
Run: `powershell.exe -NoProfile -Command "dotnet build AlgoTradeForge.slnx"`
Expected: clean, 0 errors, 0 new warnings.

- [ ] **Step 7: Commit** (controller performs this)

```bash
git add src/AlgoTradeForge.Domain/Strategy/Subscriptions/ \
        tests/AlgoTradeForge.Domain.Tests/Strategy/Subscriptions/SubscriptionResolverTests.cs
git commit -F - <<'EOF'
feat(domain): resolved Asset + accessors + single resolver on DataFeedSubscription

§A prep (additive). DataFeedSubscription gains [JsonIgnore] Asset/IsExportable;
RequireAsset()/FeedKey()/ResolveExecutionAsset() accessors; SubscriptionResolver.Resolve.
DataSubscription untouched — substitution happens next.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

### Task 2: Atomic flip — substitute `DataFeedSubscription` for `DataSubscription` across the public solution

**This is the large, type-system-forced atomic task.** Mostly mechanical (type-name substitution + access-pattern updates); the semantic deltas are called out explicitly. The whole public solution (`AlgoTradeForge.slnx`) compiles green only when every consumer is flipped — so there is no green sub-step; build at the end. Guarded by the full test suite + backtest-golden.

**Files (inventory from the as-built exploration — every `DataSubscription` reference in `src/` + main-repo tests):**
- **Domain core:** `Strategy/IStrategy.cs`, `Strategy/IInt64BarStrategy.cs`, `Strategy/ITradeTickStrategy.cs`, `Strategy/StrategyBase.cs`, `Strategy/StrategyParamsBase.cs`, `Strategy/Modules/ModularStrategyBase.cs`, `Strategy/Modules/StrategyContextBase.cs`, `Strategy/Modules/IFilterModule.cs`, `Strategy/Modules/Filter/AtrVolatilityFilterModule.cs`, `Strategy/Modules/Filter/RegimeFilterModule.cs`, `Strategy/Modules/Regime/RegimeDetectorModule.cs`, `Strategy/Modules/CrossAsset/CrossAssetModule.cs`, all `Strategy/*/*Strategy.cs` (BuyAndHold, DonchianBreakout, PrevBarBreakout, Rsi2MeanReversion, PairsTrading), `Indicators/IIndicatorFactory.cs`, `Indicators/PassthroughIndicatorFactory.cs`, `Engine/BacktestEngine.cs`, `Abstractions/IHistoryRepository.cs` (the legacy `Load(DataSubscription…)` overload).
- **Delete:** `Domain/History/MarketDataSnapshot.cs` + `tests/AlgoTradeForge.Domain.Tests/History/MarketDataSnapshotTests.cs`.
- **Application:** `Indicators/EmittingIndicatorFactory.cs`, `Indicators/EmittingIndicatorDecorator.cs`, `Backtests/BacktestPreparer.cs`, `Backtests/StrategySubscriptionFactory.cs` (replace `FromPrimary` with `SubscriptionResolver.Resolve`), `Optimization/OptimizationSetupHelper.cs`, `Optimization/OptimizationTaskExecutor.cs`, `Optimization/BoundedTrialQueue.cs`, `Optimization/ParameterKeyBuilder.cs`, `Debug/StartDebugSessionCommandHandler.cs`.
- **Infrastructure:** `History/HistoryRepository.cs`.
- **LiveHost:** `Domain/Live/LiveSessionConfig.cs`, `LiveHost.Application/Live/StartLiveSessionCommandHandler.cs`, `LiveHost.Application/Live/DataPlane/LiveSessionRegistration.cs`, `LiveHost.Application/Live/DataPlane/SessionSnapshotBars.cs`, `LiveHost.Application/Live/LiveSessionSnapshot.cs`, `LiveHost.Infrastructure/Live/DataPlane/BarInterest.cs`, `LiveHost.Infrastructure/Live/DataPlane/TickInterest.cs`, `LiveHost.Infrastructure/Live/DataPlane/SessionInterest.cs`, `LiveHost.Infrastructure/Live/DataPlane/StrategyDispatch.cs`, `LiveHost.Infrastructure/Live/Binance/BinanceLiveConnector.cs`, `LiveHost.Infrastructure/Live/LiveOrderContext.cs`.
- **Tests:** all main-repo test files referencing `DataSubscription` (~74) + the test-utility strategies (`BuyOnFirstBarStrategy`, `TestnetOrderStrategy` ×2, `TestnetE2EStrategy`).

**Interfaces:**
- Consumes: Task 1's `Asset`/`RequireAsset()`/`FeedKey()`/`ResolveExecutionAsset()`/`SubscriptionResolver.Resolve`.
- Produces: `IStrategy.DataSubscriptions : IList<DataFeedSubscription>`; `IInt64BarStrategy.OnBarStart/OnBarComplete(Int64Bar, DataFeedSubscription)`; `ITradeTickStrategy.OnTradeTick(in TradeTick, DataFeedSubscription)`; `IIndicatorFactory.Create<…>(…, DataFeedSubscription)`; `LiveSessionConfig { IReadOnlyList<DataFeedSubscription> Subscriptions; … }` (no `RawSubscriptions`, no `PrimaryAsset`); `LiveSessionRegistration(…, IReadOnlyList<DataFeedSubscription> Subscriptions, …)` (no raw list); `BarInterest(string, BarSpecKey, DataFeedSubscription)`; `TickInterest(string, DataFeedSubscription)`.

**Transformation rules (apply uniformly):**
1. Every signature/field/local typed `DataSubscription` → `DataFeedSubscription`.
2. `sub.Asset` → `sub.RequireAsset()` at *consumption* sites that need a non-null `Asset` (order sizing, `ScaleContext`, `EmitBar`, `HistoryRepository.Load`). Leave `sub.Asset` where a nullable is acceptable.
3. `sub.FeedKey` (field) → `sub.FeedKey()` (extension).
4. `sub.IsExportable` → unchanged (now a property on the base).
5. `sub.TimeFrame` → for `EmitBar`/`HistoryRepository.Load`/`ParameterKeyBuilder`, switch on subtype: `TimeBarSubscription tb ? tb.TimeFrame : …` — for non-TimeBar use `FeedKey()` as the event/string discriminator (preserve current `EmitBar` output for TimeBar; for alt-bar/tick emit the feed-id/"ticks" string the formatter would have produced from the old placeholder — confirm against the golden tests).
6. Construction `new DataSubscription(asset, tf, feedKey, exportable)` → `SubscriptionResolver.Resolve(spec, asset)` where a `spec` exists, or `spec with { Asset = asset, IsExportable = exportable }`.
7. `StrategySubscriptionFactory.FromPrimary(spec, asset)` callers → `SubscriptionResolver.Resolve(spec, asset)`; delete `FromPrimary` (and the file if it has no other members).
8. Routing equality (`ModularStrategyBase.IndexOf`, `CrossAssetModule sub == _sub1`, `PairsTrading ReferenceEquals`) — leave as-is; the same resolved instance is delivered, so value/reference equality holds.

**Semantic deltas (NOT mechanical — implement deliberately):**
- **`LiveSessionConfig`** collapses to one resolved `IReadOnlyList<DataFeedSubscription> Subscriptions`; delete `RawSubscriptions` and `PrimaryAsset`. Add nothing for the asset — consumers call `Subscriptions.ResolveExecutionAsset()`.
- **`StartLiveSessionCommandHandler`**: build the single resolved list via `SubscriptionResolver.Resolve(spec, asset)` per command subscription (delete the inline switch); set `strategy.DataSubscriptions`; the `ScaleContext`/`InitialCash` scaling keys off `subs.ResolveExecutionAsset()` instead of `resolvedSubscriptions[0].Asset`.
- **`BinanceLiveConnector`/`LiveOrderContext`**: replace `config.PrimaryAsset` / `entry.PrimaryAsset` / `_primaryAsset` with the execution asset derived from the session's subscription list (`ExecutionAsset`), stored once on `LiveSessionEntry.ExecutionAsset` at registration. All sites from the map: `:251,262,329` (scale/balance), `:281` (portfolio), `:417` (cancel-all), `:500,518` (reconciliation), `:593` (fill parsing), and `LiveOrderContext.cs:225` (order submission).
- **`SessionInterest.Build` / `SessionSnapshotBars.Build`**: delete the dual-list length-guards; iterate the single resolved list; build `BarInterest`/`TickInterest` from each resolved subscription directly (kind via subtype pattern-match, asset via the resolved `Asset`).
- **`BacktestEngine.EmitBar`**: keep `BarEvent` output identical for TimeBar; for alt-bar/tick derive the timeframe-string via the subtype/`FeedKey()` (verify against golden).
- **Delete** `MarketDataSnapshot.cs` + its test.

- [ ] **Step 1: Apply the type substitution + semantic deltas across the inventory**

Work Domain → Application → Infrastructure → LiveHost → tests. Apply the transformation rules; implement the semantic deltas as specified. Do not build until the cluster is internally consistent (intermediate builds will fail — that's expected for an atomic flip). Keep a checklist of the inventory files; tick each as flipped.

- [ ] **Step 2: Delete the dead type**

```bash
git rm src/AlgoTradeForge.Domain/History/MarketDataSnapshot.cs \
       tests/AlgoTradeForge.Domain.Tests/History/MarketDataSnapshotTests.cs
```
And delete `src/AlgoTradeForge.Domain/Strategy/DataSubscription.cs` once no references remain.

- [ ] **Step 3: Build the whole solution**

Run: `powershell.exe -NoProfile -Command "dotnet build AlgoTradeForge.slnx"`
Expected: clean. Iterate on residual compile errors (this is the bulk of the work). Confirm **zero** remaining `DataSubscription` references: `git grep -n "DataSubscription" -- 'src/**/*.cs'` returns nothing.

- [ ] **Step 4: Run the full test suite (backtest-golden guard)**

Run sequentially (one process each):
`powershell.exe -NoProfile -Command "dotnet test tests/AlgoTradeForge.Domain.Tests/"`
`powershell.exe -NoProfile -Command "dotnet test tests/AlgoTradeForge.Application.Tests/"`
`powershell.exe -NoProfile -Command "dotnet test tests/AlgoTradeForge.Infrastructure.Tests/"`
`powershell.exe -NoProfile -Command "dotnet test tests/AlgoTradeForge.LiveHost.Application.Tests/"`
`powershell.exe -NoProfile -Command "dotnet test tests/AlgoTradeForge.LiveHost.Infrastructure.Tests/"`
`powershell.exe -NoProfile -Command "dotnet test tests/AlgoTradeForge.WebApi.Tests/"`
Expected: all green. The engine/strategy tests in Domain/Application ARE the backtest-golden guard — if any backtest-output assertion changes, the flip altered behavior; fix the flip, not the test. Test-only inline strategies / fixtures may be edited freely to match the new signatures ("break strategies freely").

- [ ] **Step 5: Commit** (controller performs this)

```bash
git add -A
git commit -F - <<'EOF'
refactor(domain,live): retire DataSubscription onto resolved DataFeedSubscription

§A + §A′. Substitute DataFeedSubscription for flat DataSubscription across the
whole public solution: strategy callbacks, engine, indicators, modules,
optimization, persistence, and the live data plane. Collapse LiveSessionConfig
to one resolved list; derive ExecutionAsset (Role==Primary, index-0 fallback)
in place of PrimaryAsset; unify the two resolvers onto SubscriptionResolver.Resolve;
delete the dual-list length-guards and the dead MarketDataSnapshot. Backtest
behavior unchanged (engine/golden suites green).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

### Task 3: Flip the Private solution

Separate build (`AlgoTradeForge.Full.slnx`) referencing the public projects — so it flips in its own commit after Task 2.

**Files (from the exploration):**
- `../AlgoTradeForge.Private/src/AlgoTradeForge.Strategies.Private/PivotBreakout/PivotBreakoutStrategyBase.cs` (`CreateSwingDetector(DataSubscription)` ×abstract + `OnStrategyInit`/`EvaluateEntry`)
- `…/PivotTrendBreakout/PivotTrendBreakoutStrategyBase.cs` (`OnBarCompleteInner(Int64Bar, DataSubscription)`, `CreateSwingDetector`)
- `…/ZigZagBreakout/ZigZagBreakoutStrategy.cs`, `…/AtrZigZagBreakout/AtrZigZagBreakoutStrategy.cs`, `…/ZigZagTrendBreakout/ZigZagTrendBreakoutStrategy.cs`, `…/AtrZigZagTrendBreakout/AtrZigZagTrendBreakoutStrategy.cs` (all `CreateSwingDetector(DataSubscription)`)
- `…/Modules/Signal/ArimaForecastFilterModule.cs` (`Initialize(IIndicatorFactory, DataSubscription)`)
- Private tests (~6 files referencing `DataSubscription`).

**Interfaces:**
- Consumes: the flipped public signatures from Task 2 (all `DataFeedSubscription`).

- [ ] **Step 1: Apply the same transformation rules to the Private sources + tests**

Same rules as Task 2 (type swap; `.Asset` → `.RequireAsset()` at sizing sites; `.FeedKey` → `.FeedKey()`). `CreateSwingDetector(DataFeedSubscription sub)`; `OnBarCompleteInner(Int64Bar, DataFeedSubscription)`.

- [ ] **Step 2: Build the Full solution**

Run: `powershell.exe -NoProfile -Command "dotnet build ../AlgoTradeForge.Private/AlgoTradeForge.Full.slnx"`
Expected: clean. Confirm no `DataSubscription` remains: `git -C ../AlgoTradeForge.Private grep -n "DataSubscription"` returns nothing.

- [ ] **Step 3: Run the Private tests**

Run: `powershell.exe -NoProfile -Command "dotnet test ../AlgoTradeForge.Private/tests/AlgoTradeForge.Strategies.Private.Tests/"`
Expected: all green.

- [ ] **Step 4: Commit** (controller performs this — note: separate repo)

```bash
git -C ../AlgoTradeForge.Private add -A
git -C ../AlgoTradeForge.Private commit -F - <<'EOF'
refactor(strategies): flip private strategies onto resolved DataFeedSubscription

Mirror the public §A retire of DataSubscription: CreateSwingDetector and
OnBarCompleteInner now take DataFeedSubscription; .Asset → RequireAsset() at
sizing sites.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_018w22NAfM8bQwp5TTiMMGbX
EOF
```

---

### Task 4: Backtest-golden + benchmark verification

Confirm the callback-type change did not perturb the engine hot path.

**Files:** none (verification only); may add a perf-history baseline note.

- [ ] **Step 1: Capture the post-change benchmark**

Run (no other `dotnet` process active): `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/save-baseline.ps1 -Filter '*Backtest_5y*' -Label 'post-subscription-unify'`
Then compare against the most recent pre-change baseline:
`powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/perf/compare-baseline.ps1 -Baseline previous -Candidate latest`
Expected: Mean + Allocated within noise. If `DataFeedSubscription` (a polymorphic record) measurably regresses allocation vs the old flat record on the per-bar callback path, record the delta and flag for the final review.

- [ ] **Step 2: Final whole-effort confirmation**

Confirm: `git grep -n "DataSubscription" -- 'src/**/*.cs' 'tests/**/*.cs'` and the Private equivalent both return nothing; `MarketDataSnapshot` is gone; `LiveSessionConfig` has no `PrimaryAsset`/`RawSubscriptions`. Record the benchmark delta in the SDD ledger.

(No commit unless a baseline note file is added.)

---

## Self-Review

**Spec coverage:**
- Unified type / `[JsonIgnore] Asset` / `RequireAsset`/`FeedKey` (spec §1) → Task 1 + Task 2 rules 1–6. ✅
- One resolver (spec §2) → Task 1 (`SubscriptionResolver`) + Task 2 rule 7. ✅
- `ExecutionAsset` + index-0 fallback (spec §3) → Task 1 (`ResolveExecutionAsset`) + Task 2 semantic deltas. ✅
- §B param retype only, `IInt64BarStrategy` unchanged (spec §4) → Task 2 (signatures retyped, interface kept). ✅
- `MarketDataSnapshot` deleted (spec §5) → Task 2 Step 2. ✅
- Full consumer list (spec §6) → Task 2 inventory + Task 3 (Private). ✅
- Equality/routing preserved (spec §7) → Task 2 rule 8. ✅
- Guards: backtest-golden, Private green, benchmarks (spec Testing) → Task 2 Step 4, Task 3, Task 4. ✅

**Placeholder scan:** Task 1 carries complete code. Task 2/3 are an inventory + precise transformation rules + explicit semantic deltas rather than per-file code — unavoidable for a 70+-file mechanical substitution, but every non-mechanical change is specified concretely (no "handle edge cases"). The one soft spot: the test asset factory name in Task 1 (`CryptoAsset.Create`) must be confirmed against sibling tests — flagged inline in Step 1/2.

**Type consistency:** `DataFeedSubscription` (resolved), `RequireAsset()`, `FeedKey()`, `ResolveExecutionAsset()`, `SubscriptionResolver.Resolve` — names identical across Task 1 definitions, Task 2 rules, and Task 3. `IInt64BarStrategy` retained verbatim. ✅
