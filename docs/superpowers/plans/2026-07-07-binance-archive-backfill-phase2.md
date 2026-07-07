# Binance Archive Backfill — Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Flip replenishable feeds to lazy-by-default collection (per-feed `"Eager": true` opt-back), extend the frontend Data tab with archive coverage + on-demand load jobs, and add a candle-coverage hint to the Launch panel.

**Spec:** `docs/superpowers/specs/2026-07-07-binance-archive-backfill-design.md` §7.2 + §1/§3/§4 (read it first).

**Base:** `main` @ 6e0748b (phase 1 merged). Branch: `feat/archive-backfill-phase2`.

**Architecture:** A new `CollectionPolicy` (Application) derives eager/lazy per `(asset, feed)` from `ArchiveMaterializerRegistry` — the registry stays the single source of replenishability truth. The policy gates the scheduled-collector inner loop AND the stream-service symbol-set builders. The main WebApi's `/api/data/*` proxy (byte-identical pass-through) gains `coverage` + `loads` routes; the frontend consumes them snake_case verbatim. Load-job progress is **polled** (`GET /loads/{id}` — no SSE upstream), unlike aggregation jobs.

**Tech Stack:** C# 14 / .NET 10, xunit.v3 + NSubstitute; frontend Next.js 16 / TanStack Query 5 / Zustand 5 / Vitest 4 (`frontend/`).

## Global Constraints

- **Commit mode (owner-approved):** each implementer commits ONLY files it created/modified for its task. `git add <explicit paths>` — NEVER `-A`, never `docs/superpowers/**`, never `.superpowers/**`, never README.md. Commit messages end with trailers:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` and `Claude-Session: <session url provided by controller>`.
- **Only ONE `dotnet` process at a time.** Never run build/test in parallel. Frontend `npm test` runs from `frontend/`.
- Backend test command: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~<TestClass>"` (WebApi proxy tests: `tests/AlgoTradeForge.WebApi.Tests/`).
- No `Async` suffix on new async methods; `CancellationToken ct = default` on every async signature.
- One type per file, named after the type (single-line records accompanying an interface may share its file).
- Comments: only non-obvious facts, terse. Warnings are errors.
- **Tests:** pass `TestContext.Current.CancellationToken` to every awaited call in test bodies (xUnit1051 — snippets below omit it for brevity; add it when transcribing).
- **API JSON is snake_case** (global `SnakeCaseLower` in HistoryLoader `Program.cs:49-54`). The `/api/data/*` proxy round-trips bytes — the frontend consumes snake_case verbatim (`frontend/types/data-tab.ts` header rule: NO camelCase converter).
- **Frontend copy is English** ("Load", "Continue" — the spec's «догрузить» maps to English labels; zero Cyrillic in the codebase).
- **Closed-months rule (load-bearing):** the archive never covers the current UTC month — it is REST/stream-tail-owned. Every "missing months" computation in the FE MUST exclude the current month, or banners will never clear.
- Frontend types for `/api/data/*`: hand-written interfaces in `types/data-tab.ts`, snake_case fields.

## Existing interfaces you will consume (verified signatures)

```csharp
// Application.Archive — the classification source of truth
public sealed class ArchiveMaterializerRegistry {
    public IArchiveMaterializer? Resolve(string exchange, string feedName, string assetType);
    public bool IsReplenishable(string exchange, string feedName, string assetType);
}
// Registered materializers (DependencyInjection.cs:185-226): candles (spot+futures),
// mark-price (futures), open-interest / ls-ratio-global / ls-ratio-top-accounts /
// ls-ratio-top-positions (futures). NOT registered (=> irreplaceable today): funding-rate,
// premium-index, index-price, taker-volume, liquidations, ticks, book-ticker.

// Application/HistoryLoaderOptions.cs
public sealed class FeedCollectionConfig {
    public required string Name { get; init; }
    public string Interval { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public DateOnly? HistoryStart { get; init; }
    public double GapThresholdMultiplier { get; init; } = 2.0;
}
public sealed class AssetCollectionConfig {
    public required string Symbol { get; init; }
    public string Exchange { get; init; } = "binance";
    public required string Type { get; init; }   // "spot" | "perpetual" | "future" | "equity"
    public int DecimalDigits { get; init; } = 2;
    public DateOnly HistoryStart { get; init; } = new(2020, 1, 1);
    public List<FeedCollectionConfig> Feeds { get; init; } = [];
}

// WebApi/Collection/ScheduledCollectorService.cs — base of 6 collectors
// (Kline daily FuturesOnly=false; FundingRate cron; Oi 5m; Ratio 15m [ls-global,
//  ls-top-accounts, taker-volume]; Hourly 1h [mark-price, premium-index, index-price,
//  ls-top-positions]; Ticks 5m). Inner dispatch loop (CollectCycleAsync, currently private):
//   var feeds = asset.Feeds.Where(f => f.Enabled && f.Name == feedName);
//   ... await symbolCollector.CollectFeedAsync(asset, feed, assetDir, fromMs, toMs, ct: ct);

// WebApi/Collection/BookTickerStreamService.cs:361 (today)
private static List<string> BuildEnabledSymbols(HistoryLoaderOptions config, Func<string, bool> typeFilter);
// SpotAggTradeStreamService has the analogous BuildEnabledSpotSymbols for FeedNames.Ticks.

// Application/Archive/Jobs — the wire snapshot (GET /api/v1/loads/{id} body)
public sealed record LoadJobSnapshot(
    string JobId, LoadJobState State, DateTimeOffset QueuedAt, DateTimeOffset? CompletedAt,
    int MonthsDone, int MonthsTotal, string? CurrentMonth, string? ErrorCode, string? ErrorMessage,
    string Symbol, string FeedName, string Interval, DateOnly From, DateOnly To);
public enum LoadJobState { Queued, Running, Complete, Error }  // serializes as INT today — Task 4 fixes

// Infrastructure/Archive/MonthCoverageCalculator.cs
public interface IMonthCoverageCalculator {
    Task<bool> IsMonthCovered(string assetDir, string feedName, string interval,
        int year, int month, IReadOnlyList<DataGap> gaps,
        long? effectiveStartMs = null, CancellationToken ct = default);
}

// Main WebApi Data proxy plumbing (src/AlgoTradeForge.WebApi/):
//   Endpoints/DataEndpoints.cs — MapGroup("/api/data"); helpers ProxyPassthroughGet(ctx, client, path),
//     WriteProblem; DataProxyProblem.Unavailable/Timeout/UpstreamError; upstream 4xx forwarded byte-identical.
//   Data/HistoryLoaderClient.cs — GetAsync(path, ct), PostJsonAsync(path, JsonElement, ct).
//   Tests: tests/AlgoTradeForge.WebApi.Tests/Data/DataProxyTestFactory.cs (WebApplicationFactory<Program>
//     with CapturingHandler replacing the typed client's primary handler) + DataProxyTests.cs style.
```

**HistoryLoader wire contracts the FE will consume (all snake_case):**

- `GET /api/v1/coverage?exchange=&symbol=&asset_type=` → `{ asset_dir, feeds: [{ feed_name, interval, covered_months: ["yyyy-MM"...], first_timestamp: long|null, last_timestamp: long|null }] }`. `symbol` here is the DISPLAY symbol ("BTCUSDT") + `asset_type` — NOT the directory name. 422 `{error, message}` on bad path component / unknown asset type.
- `POST /api/v1/loads` body `{exchange, symbol, asset_type, feed_name, interval, from, to}` (`from`/`to` = `"yyyy-MM-dd"`). 202 `{job_id}`; 409 `{error: "symbol_busy"|"feed_busy", active_job_id}`; 422 `{error, message}` (codes: invalid_path_component, unknown_asset_type, invalid_range, too_many_months, invalid_interval, not_replenishable); 503 `{error: "queue_full"}`.
- `GET /api/v1/loads/{jobId}` → LoadJobSnapshot fields snake_cased (`job_id, state, queued_at, completed_at, months_done, months_total, current_month, error_code, error_message, symbol, feed_name, interval, from, to`); 404 `{error: "job_not_found_or_expired", job_id}`.

**Frontend catalog trap (CORRECTED post-live-smoke):** `AssetCatalogEntry.symbol` is the on-disk directory name (`"BTCUSDT_perp"`), `display_name` is a UI label — `"BTCUSDT-perp"` WITH A DASH for perpetuals (the plan originally said `"BTCUSDT"`; the live smoke refuted that and commit 58fdcd1 fixed it). Coverage/load requests need the EXCHANGE symbol = `exchangeSymbolOf(asset)` (directory symbol minus `_perp`) + `type`; `display_name` must never be used as an API key. Catalog lookups and `/api/data/exchanges/{e}/assets/{a}/...` routes use `symbol`. TimeBar catalog feed `id` == its interval (`"1h"`); coverage entry for it is `feed_name == "candles" && interval == id`.

**Operational decision (owner-approved at plan review): candles stay eager.** Task 2 adds `"Eager": true` to EVERY `candles` feed entry in `appsettings.json` (all symbols, spot and perpetual). Rationale — the stale-tail hole: the current-month REST tail is only filled inside `CollectFeedAsync`, which under full-lazy runs only when a load job fires; the FE closed-months rule (correctly) never flags the current month as missing; so lazy candles would silently serve week-old bars to a backtest ending "today" with no banner. Keeping candles eager preserves the daily tail refresh and matches the spec's cloud-profile follow-up (klines eager for live warm-up). All OTHER configured replenishable cron feeds (open-interest, ls-ratio-global, ls-ratio-top-accounts, mark-price, ls-ratio-top-positions) go lazy as designed. `funding-rate`, `premium-index`, `index-price`, `taker-volume`, `liquidations`, `ticks`, spot `book-ticker` keep collecting (irreplaceable today).

---

### Task 1: `Eager` config flag + `CollectionPolicy`

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs` (add one property to `FeedCollectionConfig`)
- Create: `src/AlgoTradeForge.HistoryLoader.Application/Collection/CollectionPolicy.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (one DI line)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Collection/CollectionPolicyTests.cs`

**Interfaces:**
- Consumes: `ArchiveMaterializerRegistry.IsReplenishable(exchange, feedName, assetType)`.
- Produces: `FeedCollectionConfig.Eager` (bool, default false); `CollectionPolicy.IsEagerlyCollected(AssetCollectionConfig asset, FeedCollectionConfig feed) : bool`. Tasks 2 and 3 depend on these exact names.

- [ ] **Step 1: Write the failing tests**

```csharp
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class CollectionPolicyTests
{
    private static IArchiveMaterializer Materializer(string exchange, string feed, bool supportsSpot = true)
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns(exchange);
        m.FeedName.Returns(feed);
        m.Supports(Arg.Any<string>()).Returns(ci =>
            supportsSpot || AlgoTradeForge.HistoryLoader.Domain.AssetTypes.IsFutures(ci.Arg<string>()));
        return m;
    }

    private static AssetCollectionConfig Asset(string type = "perpetual") =>
        new() { Symbol = "BTCUSDT", Type = type };

    [Fact]
    public void ReplenishableFeed_WithoutOverride_IsLazy()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([Materializer("binance", "candles")]));
        Assert.False(policy.IsEagerlyCollected(Asset(), new FeedCollectionConfig { Name = "candles", Interval = "1h" }));
    }

    [Fact]
    public void ReplenishableFeed_WithEagerTrue_IsEager()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([Materializer("binance", "candles")]));
        Assert.True(policy.IsEagerlyCollected(Asset(),
            new FeedCollectionConfig { Name = "candles", Interval = "1h", Eager = true }));
    }

    [Fact]
    public void IrreplaceableFeed_IsAlwaysEager()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([]));
        Assert.True(policy.IsEagerlyCollected(Asset(), new FeedCollectionConfig { Name = "liquidations" }));
    }

    [Fact]
    public void AssetTypeSensitivity_FuturesOnlyMaterializer_LeavesSpotEager()
    {
        var registry = new ArchiveMaterializerRegistry([Materializer("binance", "book-ticker", supportsSpot: false)]);
        var policy = new CollectionPolicy(registry);
        Assert.True(policy.IsEagerlyCollected(Asset("spot"), new FeedCollectionConfig { Name = "book-ticker" }));
        Assert.False(policy.IsEagerlyCollected(Asset("perpetual"), new FeedCollectionConfig { Name = "book-ticker" }));
    }

    [Fact]
    public void UnknownExchange_IsEager()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([Materializer("binance", "candles")]));
        var asset = new AssetCollectionConfig { Symbol = "AAPL", Exchange = "ib", Type = "equity" };
        Assert.True(policy.IsEagerlyCollected(asset, new FeedCollectionConfig { Name = "candles" }));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~CollectionPolicyTests"`
Expected: FAIL — `CollectionPolicy` not defined / `Eager` not defined.

- [ ] **Step 3: Implement**

In `HistoryLoaderOptions.cs`, add to `FeedCollectionConfig` (after `GapThresholdMultiplier`):

```csharp
    /// <summary>Opts a replenishable feed back into scheduled/stream collection (spec §1).</summary>
    public bool Eager { get; init; }
```

Create `CollectionPolicy.cs`:

```csharp
using AlgoTradeForge.HistoryLoader.Application.Archive;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

/// <summary>
/// Eager/lazy collection decision (spec §1). Replenishable feeds — an archive materializer
/// exists for (exchange, feed, assetType) — default to lazy on-demand loading; per-feed
/// "Eager": true opts back in. Irreplaceable feeds are always eager: skipping them loses
/// data forever. Governs cron collectors AND stream startup symbol sets alike.
/// </summary>
public sealed class CollectionPolicy(ArchiveMaterializerRegistry registry)
{
    public bool IsEagerlyCollected(AssetCollectionConfig asset, FeedCollectionConfig feed) =>
        feed.Eager || !registry.IsReplenishable(asset.Exchange, feed.Name, asset.Type);
}
```

In `Program.cs`, next to `builder.Services.AddSingleton<SymbolCollector>();` (line ~94):

```csharp
builder.Services.AddSingleton<CollectionPolicy>();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~CollectionPolicyTests"`
Expected: 5/5 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/HistoryLoaderOptions.cs \
        src/AlgoTradeForge.HistoryLoader.Application/Collection/CollectionPolicy.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Collection/CollectionPolicyTests.cs
git commit -m "feat(archive): Eager feed flag + CollectionPolicy (lazy-by-default core)"
```

---

### Task 2: Eager gate in the scheduled collectors

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/ScheduledCollectorService.cs`
- Modify (ctor threading, one line each): `KlineCollectorService.cs`, `FundingRateCollectorService.cs`, `OiCollectorService.cs`, `RatioCollectorService.cs`, `HourlyCollectorService.cs`, `TicksCollectorService.cs` (same folder)
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/appsettings.json` (candles stay eager — see Global decision)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Collection/ScheduledCollectorEagerGateTests.cs`

**Interfaces:**
- Consumes: `CollectionPolicy.IsEagerlyCollected(asset, feed)` (Task 1).
- Produces: `ScheduledCollectorService` primary ctor gains `CollectionPolicy collectionPolicy` as the SECOND parameter; `CollectCycleAsync` visibility changes `private` → `internal` (test hook, matching the endpoints' "internal for direct endpoint-level testing" convention).

The gate must sit in the **inner per-feed loop** — `RatioCollectorService` and `HourlyCollectorService` each collect a mix of replenishable and irreplaceable feeds, so a per-service switch would be wrong.

- [ ] **Step 1: Write the failing test**

The arrange section builds a real `SymbolCollector` with NSubstitute internals — copy the construction helper from the existing `tests/AlgoTradeForge.HistoryLoader.Tests/Collection/SymbolCollectorTests.cs` fixture verbatim (same substitutes: `IFeedCollector`, `ISettingsWriter`, `IFeedStatusStore`, `IMonthCoverageCalculator`, `TestClock`, `NullLogger`, and an `ArchiveBackfillService` built over an EMPTY `ArchiveMaterializerRegistry` so `CollectFeedAsync` goes straight to the REST path). The POLICY gets a SEPARATE registry that DOES contain a candles materializer — classification and archive execution stay decoupled in the test.

```csharp
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class ScheduledCollectorEagerGateTests
{
    // BuildSymbolCollector(out IFeedCollector candleCollector): copy from SymbolCollectorTests.

    private static (KlineCollectorService Service, IFeedCollector CandleCollector) Build(
        bool eager, ArchiveMaterializerRegistry policyRegistry, string dataRoot)
    {
        var (symbolCollector, candleCollector) = BuildSymbolCollector();
        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions
        {
            DataRoot = dataRoot,
            Assets =
            [
                new AssetCollectionConfig
                {
                    Symbol = "BTCUSDT", Type = "perpetual",
                    Feeds = [new FeedCollectionConfig { Name = "candles", Interval = "1h", Eager = eager }],
                },
            ],
        });
        var breaker = Substitute.For<ICollectionCircuitBreaker>();
        breaker.IsTripped.Returns(false);
        var service = new KlineCollectorService(
            symbolCollector, new CollectionPolicy(policyRegistry), breaker,
            Substitute.For<IHttpClientFactory>(), options,
            NullLogger<KlineCollectorService>.Instance);
        return (service, candleCollector);
    }

    [Fact]
    public async Task ReplenishableFeed_Lazy_IsSkippedByScheduledCycle()
    {
        var registry = RegistryWithCandlesMaterializer(); // helper: substitute IArchiveMaterializer binance/candles
        var (service, candleCollector) = Build(eager: false, registry, dataRoot: TestDir());
        await service.CollectCycleAsync(CancellationToken.None);
        await candleCollector.DidNotReceiveWithAnyArgs().CollectAsync(
            default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task ReplenishableFeed_EagerOverride_IsCollected()
    {
        var registry = RegistryWithCandlesMaterializer();
        var (service, candleCollector) = Build(eager: true, registry, dataRoot: TestDir());
        await service.CollectCycleAsync(CancellationToken.None);
        await candleCollector.ReceivedWithAnyArgs(1).CollectAsync(
            default!, default!, default!, default, default, default);
    }

    [Fact]
    public async Task IrreplaceableFeed_NoMaterializer_IsCollectedWithoutOverride()
    {
        var (service, candleCollector) = Build(
            eager: false, new ArchiveMaterializerRegistry([]), dataRoot: TestDir());
        await service.CollectCycleAsync(CancellationToken.None);
        await candleCollector.ReceivedWithAnyArgs(1).CollectAsync(
            default!, default!, default!, default, default, default);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~ScheduledCollectorEagerGateTests"`
Expected: FAIL — ctor has no `CollectionPolicy` parameter, `CollectCycleAsync` inaccessible.

- [ ] **Step 3: Implement**

`ScheduledCollectorService.cs`: primary ctor becomes

```csharp
internal abstract class ScheduledCollectorService(
    SymbolCollector symbolCollector,
    CollectionPolicy collectionPolicy,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger logger) : BackgroundService
```

`CollectCycleAsync` visibility `private` → `internal` with the standard comment (`// internal for direct cycle-level testing (InternalsVisibleTo)`), and the feed filter (line ~188) becomes:

```csharp
                var feeds = asset.Feeds
                    .Where(f => f.Enabled
                        && f.Name == feedName
                        && collectionPolicy.IsEagerlyCollected(asset, f));
```

In `appsettings.json`, add `"Eager": true` to every feed entry with `"Name": "candles"` — spot AND perpetual assets, all intervals — e.g.:

```json
    { "Name": "candles", "Interval": "1h", "Eager": true },
```

(Owner decision at plan review: keeps the daily kline cron + current-month REST tail alive; lazy candles would silently go stale because the FE closed-months rule never flags the current month. All other replenishable feeds get NO override — they go lazy.)

Each of the 6 subclasses adds `CollectionPolicy collectionPolicy` after `symbolCollector` in its own ctor and forwards it in `base(...)` — e.g. `KlineCollectorService.cs`:

```csharp
internal sealed class KlineCollectorService(
    SymbolCollector symbolCollector,
    CollectionPolicy collectionPolicy,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<KlineCollectorService> logger)
    : ScheduledCollectorService(symbolCollector, collectionPolicy, circuitBreaker, httpClientFactory, options, logger)
```

- [ ] **Step 4: Run the new tests, then the full HistoryLoader suite**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: all PASS (DI resolves `CollectionPolicy` via Task 1's registration; no existing test constructs the collectors directly).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Collection/*.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/appsettings.json \
        tests/AlgoTradeForge.HistoryLoader.Tests/Collection/ScheduledCollectorEagerGateTests.cs
git commit -m "feat(archive): lazy-by-default gate in scheduled collectors; candles opt back eager"
```

---

### Task 3: Eager gate in stream-service symbol sets

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/BookTickerStreamService.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Collection/SpotAggTradeStreamService.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Collection/StreamServiceEagerGateTests.cs`

**Interfaces:**
- Consumes: `CollectionPolicy` (Task 1).
- Produces: `BookTickerStreamService.BuildEnabledSymbols(HistoryLoaderOptions, Func<string,bool>, CollectionPolicy)` and `SpotAggTradeStreamService.BuildEnabledSpotSymbols(HistoryLoaderOptions, CollectionPolicy)` — both `internal static` (they are `private static` today; widen for tests, keep static, add the policy parameter).

Spec §1: the policy governs streams too. Today no materializer exists for `book-ticker` or `ticks`, so `IsEagerlyCollected` returns true for every stream feed and **behavior does not change** — the gate is wired now so phase 3's materializers activate it automatically instead of silently killing/keeping streams. `LiquidationStreamService` is intentionally NOT touched: liquidations can never become replenishable (not archived), so a policy check there is dead code.

- [ ] **Step 1: Write the failing tests**

```csharp
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class StreamServiceEagerGateTests
{
    private static HistoryLoaderOptions Config(string type, string feedName, bool eager) => new()
    {
        Assets =
        [
            new AssetCollectionConfig
            {
                Symbol = "BTCUSDT", Type = type,
                Feeds = [new FeedCollectionConfig { Name = feedName, Eager = eager }],
            },
        ],
    };

    private static ArchiveMaterializerRegistry FuturesBookTickerRegistry()
    {
        var m = Substitute.For<IArchiveMaterializer>();
        m.Exchange.Returns("binance");
        m.FeedName.Returns(FeedNames.BookTicker);
        m.Supports(Arg.Any<string>()).Returns(ci => AssetTypes.IsFutures(ci.Arg<string>()));
        return new ArchiveMaterializerRegistry([m]);
    }

    [Fact]
    public void BookTicker_NoMaterializerToday_AlwaysStreams()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([]));
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Config("perpetual", FeedNames.BookTicker, eager: false), AssetTypes.IsFutures, policy);
        Assert.Single(symbols);
    }

    [Fact]
    public void BookTicker_FuturesReplenishable_StreamsOnlyWhenEager()
    {
        var policy = new CollectionPolicy(FuturesBookTickerRegistry());
        Assert.Empty(BookTickerStreamService.BuildEnabledSymbols(
            Config("perpetual", FeedNames.BookTicker, eager: false), AssetTypes.IsFutures, policy));
        Assert.Single(BookTickerStreamService.BuildEnabledSymbols(
            Config("perpetual", FeedNames.BookTicker, eager: true), AssetTypes.IsFutures, policy));
    }

    [Fact]
    public void BookTicker_SpotIrreplaceable_AlwaysStreams()
    {
        // The futures-only materializer must not silence the spot stream (spec §1).
        var policy = new CollectionPolicy(FuturesBookTickerRegistry());
        var symbols = BookTickerStreamService.BuildEnabledSymbols(
            Config("spot", FeedNames.BookTicker, eager: false), AssetTypes.IsSpot, policy);
        Assert.Single(symbols);
    }

    [Fact]
    public void SpotAggTrades_NoMaterializerToday_AlwaysStreams()
    {
        var policy = new CollectionPolicy(new ArchiveMaterializerRegistry([]));
        var symbols = SpotAggTradeStreamService.BuildEnabledSpotSymbols(
            Config("spot", FeedNames.Ticks, eager: false), policy);
        Assert.Single(symbols);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~StreamServiceEagerGateTests"`
Expected: FAIL — methods private / wrong arity.

- [ ] **Step 3: Implement**

`BookTickerStreamService`: add `CollectionPolicy collectionPolicy` to the primary ctor (after `feedStatusStore`); change the builder to

```csharp
    internal static List<string> BuildEnabledSymbols(
        HistoryLoaderOptions config, Func<string, bool> typeFilter, CollectionPolicy policy) =>
        config.Assets
            .Where(a => typeFilter(a.Type))
            .Where(a => a.Feeds.Any(f =>
                f.Enabled && f.Name == FeedNames.BookTicker && policy.IsEagerlyCollected(a, f)))
            .Select(a => a.Symbol)
            .ToList();
```

and update the two call sites in `ExecuteAsync` to pass `collectionPolicy`. `EnsureSchemas` is driven by the returned symbol lists — no further change.

`SpotAggTradeStreamService`: same pattern — ctor param, `BuildEnabledSpotSymbols(config, policy)` becomes `internal static`, its `Feeds.Any(...)` gains `&& policy.IsEagerlyCollected(a, f)`, call site updated. Preserve each method's existing body shape; the policy conjunct is the only logic change.

- [ ] **Step 4: Run the new tests, then the full HistoryLoader suite**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: all PASS (existing stream-service tests target parse methods, unaffected).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Collection/BookTickerStreamService.cs \
        src/AlgoTradeForge.HistoryLoader.WebApi/Collection/SpotAggTradeStreamService.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Collection/StreamServiceEagerGateTests.cs
git commit -m "feat(archive): collection policy gates stream symbol sets (spec §1 stream axis)"
```

---

### Task 4: Wire-contract polish — `state` as string, null-timestamp contract pinned

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobSnapshot.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobRecord.cs` (`Snapshot()`)
- Modify: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/LoadJobRegistryTests.cs` (state assertions)
- Modify: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/CoverageEndpointTests.cs` (add null-timestamp pin if absent)
- Modify: `docs/superpowers/specs/2026-07-07-binance-archive-backfill-design.md` (one Decisions-log line)

**Interfaces:**
- Produces: `LoadJobSnapshot.State : string` with values `"queued" | "running" | "complete" | "error"` — the FE types in Task 9 depend on these exact strings. `LoadJobState` (enum) remains the internal state machine on `LoadJobRecord`.

Why: HistoryLoader registers no string-enum converter, so `state` serializes as a bare int today (0–3) — a fragile contract to hand a UI. Projecting to a lowercase string in `Snapshot()` fixes the wire without a global converter (which would silently change every other enum-bearing endpoint). Deliberate choice over `[JsonStringEnumMemberName]` on `LoadJobState` members: the projection keeps the wire contract visible at the single point where the snapshot is built, instead of depending on serializer attributes plus a per-type converter registration. Phase 1 shipped without consumers, so this is a safe contract change. Same task pins the coverage decision: absent `FeedStatus` ⇒ `first_timestamp`/`last_timestamp` are JSON **null** (already the behavior; ledger follow-up #6 wants it contractual).

- [ ] **Step 1: Update/write the failing tests**

In `LoadJobRegistryTests.cs`, change state assertions to strings (e.g. `Assert.Equal("queued", snapshot.State)`) and add:

```csharp
    [Fact]
    public void Snapshot_State_IsLowercaseWireString()
    {
        // Pin all four values — the FE union type depends on them.
        Assert.Equal("queued", StateString(LoadJobState.Queued));
        Assert.Equal("running", StateString(LoadJobState.Running));
        Assert.Equal("complete", StateString(LoadJobState.Complete));
        Assert.Equal("error", StateString(LoadJobState.Error));
        // StateString: build a LoadJobRecord, drive it to the state, return Snapshot().State.
    }
```

In `CoverageEndpointTests.cs` add (skip if an equivalent already exists):

```csharp
    [Fact]
    public async Task FeedEntry_NoFeedStatus_TimestampsAreNull()
    {
        // Arrange: manifest with one interval feed, feed dir exists with one partition file,
        // feedStatusStore.Load(...) returns null (substitute default).
        // Act: CoverageEndpoints.GetCoverage(...) → 200.
        // Assert: entry.first_timestamp is null && entry.last_timestamp is null (deserialize
        // the IResult payload via the existing test helper pattern in this file).
    }
```

(Transcribe the arrange from the file's existing tests — they already build the options/schema substitutes and temp dirs.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~LoadJobRegistryTests|FullyQualifiedName~CoverageEndpointTests"`
Expected: LoadJobRegistryTests FAIL (State is enum).

- [ ] **Step 3: Implement**

`LoadJobSnapshot.cs`:

```csharp
public sealed record LoadJobSnapshot(
    string JobId, string State, DateTimeOffset QueuedAt, DateTimeOffset? CompletedAt,
    int MonthsDone, int MonthsTotal, string? CurrentMonth, string? ErrorCode, string? ErrorMessage,
    string Symbol, string FeedName, string Interval, DateOnly From, DateOnly To);
```

`LoadJobRecord.Snapshot()`: `State: State.ToString().ToLowerInvariant(),`.

Spec doc — append to the Decisions log:

```markdown
- Coverage contract: absent `FeedStatus` ⇒ `first_timestamp`/`last_timestamp` are JSON `null` (not 0); load-job `state` is a lowercase wire string (`queued|running|complete|error`), not the enum int. Decided at phase-2 planning.
```

- [ ] **Step 4: Run the full HistoryLoader suite**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: all PASS.

- [ ] **Step 5: Commit** (spec doc edit is committed here by the CONTROLLER, not the implementer, per the no-docs-staging rule — implementer stages only src/tests)

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobSnapshot.cs \
        src/AlgoTradeForge.HistoryLoader.Application/Archive/Jobs/LoadJobRecord.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/LoadJobRegistryTests.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/CoverageEndpointTests.cs
git commit -m "fix(archive): load-job state as lowercase wire string; pin null-timestamp coverage contract"
```

---

### Task 5: Coverage scan cost — streaming row count with change-keyed cache

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/MonthCoverageCalculator.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/MonthCoverageCalculatorTests.cs` (extend)

**Interfaces:** `IMonthCoverageCalculator` unchanged. Internal change only.

Why: `IsMonthCovered` does `File.ReadAllLinesAsync` per closed month per request (ledger follow-up #3); the Data tab will poll `/coverage`. Fix has two independent halves: (a) stream-count lines instead of materializing the whole file; (b) memoize the row count keyed by `(path, FileInfo.Length, LastWriteTimeUtc)` — partitions are replaced atomically, so any content change moves both length and mtime. Cache the ROW COUNT, not the verdict: gaps and the clock participate in the verdict and can change independently of the file. The calculator is a singleton; use `ConcurrentDictionary` (bounded in practice by the number of partition files on disk).

- [ ] **Step 1: Add failing tests** (extend the existing test class; reuse its temp-dir fixture)

```csharp
    [Fact]
    public async Task RowCount_IsRecomputed_WhenPartitionFileChanges()
    {
        // month fully in the past, no gaps; 1h interval => expected 744 rows for 2024-01
        WritePartition(rows: 744);                       // helper writes header + N rows
        Assert.True(await _sut.IsMonthCovered(_assetDir, "candles", "1h", 2024, 1, []));
        WritePartition(rows: 10);                        // atomic-style rewrite (delete + write)
        Assert.False(await _sut.IsMonthCovered(_assetDir, "candles", "1h", 2024, 1, []));
    }

    [Fact]
    public async Task RowCount_CacheHit_DoesNotReReadUnchangedFile()
    {
        WritePartition(rows: 744);
        Assert.True(await _sut.IsMonthCovered(_assetDir, "candles", "1h", 2024, 1, []));
        // Second call with an exclusive write-lock held on the file: a re-read would throw,
        // a cache hit succeeds.
        using var exclusive = new FileStream(_partitionPath, FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.True(await _sut.IsMonthCovered(_assetDir, "candles", "1h", 2024, 1, []));
    }
```

- [ ] **Step 2: Run to verify the cache test fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~MonthCoverageCalculatorTests"`
Expected: `RowCount_CacheHit...` FAIL (IOException — current code re-reads).

- [ ] **Step 3: Implement**

Replace the read block in `IsMonthCovered` with:

```csharp
        long actualRows = 0;
        var fileInfo = new FileInfo(partitionPath);
        if (fileInfo.Exists)
            actualRows = await CountDataRows(fileInfo, ct);
```

and add to the class:

```csharp
    // Row counts memoized per (path, length, mtime). Partitions are replaced atomically
    // (PartitionFileWriter) or appended (BufferedPartitionWriter) — both move length+mtime,
    // so a stale entry cannot survive a content change.
    private readonly ConcurrentDictionary<string, (long Length, DateTime MtimeUtc, long Rows)> _rowCounts = new();

    private async Task<long> CountDataRows(FileInfo file, CancellationToken ct)
    {
        if (_rowCounts.TryGetValue(file.FullName, out var cached)
            && cached.Length == file.Length && cached.MtimeUtc == file.LastWriteTimeUtc)
            return cached.Rows;

        long lines = 0;
        using (var reader = new StreamReader(file.FullName))
            while (await reader.ReadLineAsync(ct) is not null)
                lines++;

        var rows = Math.Max(0, lines - 1);
        _rowCounts[file.FullName] = (file.Length, file.LastWriteTimeUtc, rows);
        return rows;
    }
```

(`using System.Collections.Concurrent;` at top. The `StreamReader` opens with default `FileShare.Read` — same sharing semantics as before.)

- [ ] **Step 4: Run the calculator suite + full suite**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: all PASS (existing coverage tests keep passing — semantics unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Infrastructure/Archive/MonthCoverageCalculator.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/MonthCoverageCalculatorTests.cs
git commit -m "perf(archive): stream+memoize partition row counts in coverage checks"
```

---

### Task 6: `FeedIdValidator` — reject drive-relative path components

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/FeedIdValidator.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Archive/FeedIdValidatorTests.cs` (create)

**Interfaces:** signatures unchanged; `TryValidatePathComponent` becomes whitelist-based.

Why: `"C:evil"` passes the current blacklist (`..`, `/`, `\`) yet `Path.Combine(dataRoot, "C:evil", ...)` resolves drive-relative, escaping DataRoot (final-review follow-up #1; shared by loads, coverage, and aggregation endpoints — fixed once here).

- [ ] **Step 1: Write the failing tests**

```csharp
using AlgoTradeForge.HistoryLoader.WebApi.Endpoints;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Archive;

public sealed class FeedIdValidatorTests
{
    [Theory]
    [InlineData("binance")]
    [InlineData("BTCUSDT")]
    [InlineData("BTCUSDT_perp")]
    [InlineData("1000SHIBUSDT")]
    [InlineData("brk.b")]
    public void PathComponent_Legitimate_Passes(string value) =>
        Assert.True(FeedIdValidator.TryValidatePathComponent(value, out _));

    [Theory]
    [InlineData("C:evil")]        // drive-relative — the gap this task closes
    [InlineData(@"C:\evil")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a..b")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    public void PathComponent_Hostile_Fails(string value) =>
        Assert.False(FeedIdValidator.TryValidatePathComponent(value, out _));

    [Fact]
    public void SourceFeedId_DriveRelative_Fails() =>
        Assert.False(FeedIdValidator.TryValidateSourceFeedId("C:1m", out _));
}
```

- [ ] **Step 2: Run to verify failure** — `C:evil`, `.`, `a b` currently pass. Expected: FAIL.

- [ ] **Step 3: Implement**

`TryValidatePathComponent` body becomes:

```csharp
        error = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "path component is required.";
            return false;
        }
        // Whitelist beats blacklisting: also kills drive-relative roots ("C:evil") that the
        // old ..// \\ checks let through.
        if (value is "." or ".." || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            error = $"'{value}' contains characters outside [A-Za-z0-9._-].";
            return false;
        }
        return true;
```

`TryValidateSourceFeedId`: extend its illegal-character check with `|| sourceFeedId.Contains(':')`.

- [ ] **Step 4: Verify the whitelist against the REAL History root** — `TryValidatePathComponent` also guards the coverage/aggregation endpoints that serve the full History root (~12k stooq equity dirs; index notations like `^spx` may exist). Scan every exchange- and asset-level directory name for characters outside `[A-Za-z0-9._-]` (resolve `<HistoryRoot>` from the running HistoryLoader config / `reference_vscode_launch`):

```powershell
Get-ChildItem <HistoryRoot> -Directory | Get-ChildItem -Directory |
  Where-Object { $_.Name -notmatch '^[A-Za-z0-9._-]+$' } | Select-Object -ExpandProperty Name -Unique
```

For every character this surfaces (likely `^`), add it to the whitelist AND to a passing `[InlineData]` case. An empty result = whitelist ships as-is. Record the scan outcome in the task report.

- [ ] **Step 5: Run new tests + full suite** (existing load/coverage traversal tests must stay green).

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: all PASS.

- [ ] **Step 6: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/FeedIdValidator.cs \
        tests/AlgoTradeForge.HistoryLoader.Tests/Archive/FeedIdValidatorTests.cs
git commit -m "fix(archive): whitelist path components — close drive-relative traversal gap"
```

---

### Task 7: WebApi composition smoke test (ValidateOnBuild-class hole)

**Files:**
- Modify: `tests/AlgoTradeForge.HistoryLoader.Tests/AlgoTradeForge.HistoryLoader.Tests.csproj` (add `Microsoft.AspNetCore.Mvc.Testing` package + verify a `ProjectReference` to the WebApi project exists)
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Composition/WebApiCompositionSmokeTests.cs` (create)

Why: Task 8 of phase 1 shipped a DI Critical (missing registrations crashed the host at startup) that endpoint-level tests structurally cannot catch (final-review follow-up #4). One test that actually builds and starts the host closes the class of bug.

**Safety-critical arrange detail:** the real `appsettings.json` ships live symbols — hosted collectors would hit Binance from a unit test. `PostConfigure<HistoryLoaderOptions>` clearing `Assets` runs before any `IOptionsMonitor.CurrentValue` materializes, so every collector/stream idles or exits. Also point `HistoryLoader:DataRoot` + `Storage:Local:DataRoot` at a temp dir. Note: host start writes a Serilog file under the WebApi content root `logs/` — acceptable (dev logs land there anyway).

- [ ] **Step 1: Write the failing test**

```csharp
using System.Net;
using AlgoTradeForge.HistoryLoader.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Composition;

public sealed class WebApiCompositionSmokeTests
{
    [Fact]
    public async Task Host_Composes_Starts_AndServesHealth()
    {
        var tempRoot = Directory.CreateTempSubdirectory("atf-composition-smoke-");
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseSetting("HistoryLoader:DataRoot", tempRoot.FullName);
                b.UseSetting("Storage:Local:DataRoot", tempRoot.FullName);
                b.UseDefaultServiceProvider(o => { o.ValidateOnBuild = true; o.ValidateScopes = true; });
                b.ConfigureTestServices(services =>
                    // Empty the symbol set BEFORE any hosted service reads CurrentValue —
                    // collectors idle instead of hitting live Binance from a unit test.
                    services.PostConfigure<HistoryLoaderOptions>(o => o.Assets.Clear()));
            });

            using var client = factory.CreateClient();   // builds + STARTS the host (hosted services construct here)
            var resp = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run to verify it compiles-and-runs** (this test "fails" only if composition is broken; on a healthy branch it should PASS immediately — that is its value as a regression net, not a red-green cycle). If `Program` is inaccessible, the existing `InternalsVisibleTo("AlgoTradeForge.HistoryLoader.Tests")` covers it; if the compiler still complains, add `public partial class Program { }` at the bottom of `Program.cs`.

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter "FullyQualifiedName~WebApiCompositionSmokeTests"`
Expected: PASS. Then sabotage-check it locally once (comment out `builder.Services.AddSingleton<CollectionPolicy>();`, rerun, expect FAIL with a DI resolution error, restore) — this proves the net catches Task-8-class bugs.

- [ ] **Step 3: Run the full suite** — `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`. Expected: all PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/AlgoTradeForge.HistoryLoader.Tests/AlgoTradeForge.HistoryLoader.Tests.csproj \
        tests/AlgoTradeForge.HistoryLoader.Tests/Composition/WebApiCompositionSmokeTests.cs
git commit -m "test(archive): WebApi composition smoke test (ValidateOnBuild + host start + /health)"
```

(If Step 2 required the `public partial class Program` marker, stage `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` too.)

---

### Task 8: Main-WebApi proxy — `/api/data/coverage` + `/api/data/loads`

**Files:**
- Modify: `src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs`
- Test: `tests/AlgoTradeForge.WebApi.Tests/Data/DataProxyLoadsTests.cs` (create; `DataProxyTests.cs` style)

**Interfaces:**
- Consumes: `HistoryLoaderClient.GetAsync/PostJsonAsync`, `ProxyPassthroughGet`, `DataProxyProblem`.
- Produces (FE routes for Tasks 9–12): `GET /api/data/coverage?exchange=&symbol=&asset_type=`, `POST /api/data/loads`, `GET /api/data/loads/{jobId}` — all byte-identical pass-throughs, uncached (coverage changes as jobs run; the 2s catalog cache stays catalog-only). No cache invalidation on `POST /loads`: a 202 changes nothing on disk yet, and the 2s TTL self-heals long before a job materializes files.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace AlgoTradeForge.WebApi.Tests.Data;

public sealed class DataProxyLoadsTests
{
    private static HttpResponseMessage JsonResp(byte[] body, HttpStatusCode code = HttpStatusCode.OK)
    {
        var resp = new HttpResponseMessage(code) { Content = new ByteArrayContent(body) };
        resp.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        return resp;
    }

    [Fact]
    public async Task Coverage_ForwardsQueryString_AndRoundTripsBytes()
    {
        var canonical = """{"asset_dir":"x","feeds":[{"feed_name":"candles","interval":"1h","covered_months":["2024-01"],"first_timestamp":null,"last_timestamp":1706745600000}]}"""u8.ToArray();
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => JsonResp(canonical));

        using var client = factory.CreateClient();
        var bytes = await client.GetByteArrayAsync(
            "/api/data/coverage?exchange=binance&symbol=BTCUSDT&asset_type=perpetual");

        Assert.Equal(canonical, bytes);
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/coverage", upstream.RequestUri!.AbsolutePath);
        Assert.Contains("asset_type=perpetual", upstream.RequestUri.Query);
    }

    [Fact]
    public async Task PostLoads_Forwards202AndBody()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(req =>
            JsonResp("""{"job_id":"abc123"}"""u8.ToArray(), HttpStatusCode.Accepted));

        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/data/loads", new
        {
            exchange = "binance", symbol = "BTCUSDT", asset_type = "perpetual",
            feed_name = "candles", interval = "1h", from = "2024-01-01", to = "2024-03-31",
        });

        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal("""{"job_id":"abc123"}""", await resp.Content.ReadAsStringAsync());
        var upstream = Assert.Single(factory.Handler.Requests);
        Assert.Equal("/api/v1/loads", upstream.RequestUri!.AbsolutePath);
        // Body forwarded byte-identical (snake_case preserved).
        Assert.Contains("\"asset_type\":\"perpetual\"", await upstream.Content!.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostLoads_Forwards409Verbatim()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ =>
            JsonResp("""{"error":"symbol_busy","active_job_id":"j9"}"""u8.ToArray(), HttpStatusCode.Conflict));
        using var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/data/loads", new { });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("active_job_id", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostLoads_MalformedJson_Returns400NotThrough()
    {
        await using var factory = new DataProxyTestFactory();
        using var client = factory.CreateClient();
        var resp = await client.PostAsync("/api/data/loads",
            new StringContent("{not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(factory.Handler.Requests); // never reached upstream
    }

    [Fact]
    public async Task GetLoad_PassesThrough_And5xxTranslates()
    {
        await using var factory = new DataProxyTestFactory();
        factory.Handler.Respond(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
            { Content = new StringContent("boom") });
        using var client = factory.CreateClient();
        var resp = await client.GetAsync("/api/data/loads/abc123");
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode); // DataProxyProblem.UpstreamError → 502-family ProblemDetails
    }
}
```

(For the 5xx assertion, mirror whatever status `DataProxyProblem.UpstreamError` actually produces — check `DataProxyTests.cs`'s existing 5xx test and copy its expected code.)

- [ ] **Step 2: Run to verify failure** — `dotnet test tests/AlgoTradeForge.WebApi.Tests/ --filter "FullyQualifiedName~DataProxyLoadsTests"`. Expected: 404s (routes absent).

- [ ] **Step 3: Implement** — in `MapDataEndpoints`, after the aggregations routes:

```csharp
        // Archive coverage + load jobs (phase 2) — uncached pass-throughs; loads poll fast
        // and coverage must reflect a finishing job immediately.
        g.MapGet("/coverage",
            (HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client, $"/api/v1/coverage{ctx.Request.QueryString.Value}"));

        g.MapPost("/loads",
            async (HttpContext ctx, HistoryLoaderClient client) =>
            {
                try
                {
                    var body = await ctx.Request.ReadFromJsonAsync<JsonElement>(ctx.RequestAborted);
                    using var upstream = await client.PostJsonAsync("/api/v1/loads", body, ctx.RequestAborted);
                    if ((int)upstream.StatusCode >= 500)
                    {
                        var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                        await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                        return;
                    }
                    ctx.Response.StatusCode = (int)upstream.StatusCode;
                    ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                    var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                    await ctx.Response.Body.WriteAsync(bytes, ctx.RequestAborted);
                }
                catch (JsonException)
                {
                    // Malformed request body must not surface as a 500 (existing PostAggregate
                    // shares this gap — follow-up, out of scope here).
                    ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await ctx.Response.WriteAsJsonAsync(new { error = "invalid_json" }, ctx.RequestAborted);
                }
                catch (HttpRequestException ex)
                {
                    await DataProxyProblem.Unavailable(ex.Message).ExecuteAsync(ctx);
                }
                catch (TaskCanceledException ex) when (!ctx.RequestAborted.IsCancellationRequested)
                {
                    await DataProxyProblem.Timeout(ex.Message).ExecuteAsync(ctx);
                }
            });

        g.MapGet("/loads/{jobId}",
            (string jobId, HttpContext ctx, HistoryLoaderClient client) =>
                ProxyPassthroughGet(ctx, client, $"/api/v1/loads/{Uri.EscapeDataString(jobId)}"));
```

- [ ] **Step 4: Run the WebApi test project** — `dotnet test tests/AlgoTradeForge.WebApi.Tests/`. Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs \
        tests/AlgoTradeForge.WebApi.Tests/Data/DataProxyLoadsTests.cs
git commit -m "feat(data-proxy): coverage + load-job pass-through routes on /api/data"
```

---

### Task 9: FE foundation — wire types, `dataApi` methods, month math

**Files:**
- Modify: `frontend/types/data-tab.ts`
- Modify: `frontend/lib/services/data-api.ts`
- Create: `frontend/lib/data/coverage.ts`
- Test: `frontend/lib/data/coverage.test.ts`

**Interfaces (produced — Tasks 10–12 depend on these exact names):**
- Types: `CoverageResponse`, `CoverageFeedEntry`, `LoadRequestBody`, `LoadAcceptedResponse`, `LoadJobSnapshotWire`, `LoadJobStateWire`.
- API: `dataApi.getCoverage(exchange, symbol, assetType, signal?)`, `dataApi.postLoad(body, signal?)`, `dataApi.getLoadJob(jobId, signal?)`.
- Helpers: `monthsInRange(fromIso, toIso): string[]`; `findMissingMonths(covered, fromIso, toIso, now?): string[]` (excludes the current UTC month — closed-months rule); `loadRangeForMonths(months): { from: string; to: string }` (first day of first month → last day of last month).

- [ ] **Step 1: Write the failing helper tests**

```ts
import { describe, expect, it } from "vitest";
import { findMissingMonths, loadRangeForMonths, monthsInRange } from "./coverage";

describe("monthsInRange", () => {
  it("expands an ISO range to UTC month keys inclusive", () => {
    expect(monthsInRange("2024-01-15T00:00:00Z", "2024-03-02T00:00:00Z"))
      .toEqual(["2024-01", "2024-02", "2024-03"]);
  });
  it("returns empty for inverted ranges", () => {
    expect(monthsInRange("2024-03-01T00:00:00Z", "2024-01-01T00:00:00Z")).toEqual([]);
  });
});

describe("findMissingMonths", () => {
  const now = new Date("2026-07-07T12:00:00Z");
  it("reports uncovered past months only", () => {
    expect(findMissingMonths(["2024-01", "2024-03"], "2024-01-01T00:00:00Z", "2024-03-31T00:00:00Z", now))
      .toEqual(["2024-02"]);
  });
  it("never demands the current month (archive owns closed months only)", () => {
    expect(findMissingMonths([], "2026-06-01T00:00:00Z", "2026-07-07T00:00:00Z", now))
      .toEqual(["2026-06"]);
  });
  it("is empty when everything closed is covered", () => {
    expect(findMissingMonths(["2026-06"], "2026-06-01T00:00:00Z", "2026-07-07T00:00:00Z", now))
      .toEqual([]);
  });
});

describe("loadRangeForMonths", () => {
  it("spans first day of first month to last day of last month", () => {
    expect(loadRangeForMonths(["2024-02", "2024-04"]))
      .toEqual({ from: "2024-02-01", to: "2024-04-30" });
  });
});
```

- [ ] **Step 2: Run to verify failure** — from `frontend/`: `npm test -- lib/data/coverage.test.ts`. Expected: FAIL (module missing).

- [ ] **Step 3: Implement**

`frontend/lib/data/coverage.ts`:

```ts
// Month math for archive coverage. All computations are UTC; month keys are "yyyy-MM".
// The archive covers CLOSED months only — the current UTC month is REST-tail-owned and
// must never be reported missing, or load banners would never clear.

export function monthsInRange(fromIso: string, toIso: string): string[] {
  const from = new Date(fromIso);
  const to = new Date(toIso);
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime()) || from > to) return [];
  const months: string[] = [];
  let y = from.getUTCFullYear();
  let m = from.getUTCMonth();
  const endY = to.getUTCFullYear();
  const endM = to.getUTCMonth();
  while (y < endY || (y === endY && m <= endM)) {
    months.push(`${y}-${String(m + 1).padStart(2, "0")}`);
    m += 1;
    if (m === 12) { m = 0; y += 1; }
  }
  return months;
}

export function findMissingMonths(
  covered: readonly string[],
  fromIso: string,
  toIso: string,
  now: Date = new Date(),
): string[] {
  const currentMonth = `${now.getUTCFullYear()}-${String(now.getUTCMonth() + 1).padStart(2, "0")}`;
  const coveredSet = new Set(covered);
  return monthsInRange(fromIso, toIso).filter((m) => m < currentMonth && !coveredSet.has(m));
}

export function loadRangeForMonths(months: readonly string[]): { from: string; to: string } {
  const first = months[0];
  const last = months[months.length - 1];
  const [y, m] = last.split("-").map(Number);
  const lastDay = new Date(Date.UTC(y, m, 0)).getUTCDate(); // day 0 of next month = last day
  return { from: `${first}-01`, to: `${last}-${String(lastDay).padStart(2, "0")}` };
}
```

`frontend/types/data-tab.ts` — append:

```ts
// ---- Archive coverage + load jobs (phase 2). Snake_case verbatim, same proxy rule. ----

export interface CoverageResponse {
  asset_dir: string;
  feeds: CoverageFeedEntry[];
}

export interface CoverageFeedEntry {
  feed_name: string;
  interval: string;
  covered_months: string[]; // "yyyy-MM", sorted ordinal
  first_timestamp: number | null; // epoch ms; null when no FeedStatus exists
  last_timestamp: number | null;
}

export interface LoadRequestBody {
  exchange: string;
  /** DISPLAY symbol ("BTCUSDT") — NOT the catalog directory name ("BTCUSDT_perp"). */
  symbol: string;
  asset_type: string;
  feed_name: string;
  interval: string;
  from: string; // "yyyy-MM-dd"
  to: string;
}

export interface LoadAcceptedResponse {
  job_id: string;
}

export type LoadJobStateWire = "queued" | "running" | "complete" | "error";

export interface LoadJobSnapshotWire {
  job_id: string;
  state: LoadJobStateWire;
  queued_at: string;
  completed_at: string | null;
  months_done: number;
  months_total: number;
  current_month: string | null;
  error_code: string | null;
  error_message: string | null;
  symbol: string;
  feed_name: string;
  interval: string;
  from: string;
  to: string;
}
```

`frontend/lib/services/data-api.ts` — add imports for the new types and three methods on `dataApi`:

```ts
  getCoverage: (exchange: string, symbol: string, assetType: string, signal?: AbortSignal) =>
    fetch(
      `${BASE_URL}/api/data/coverage?exchange=${encodeURIComponent(exchange)}&symbol=${encodeURIComponent(symbol)}&asset_type=${encodeURIComponent(assetType)}`,
      { signal },
    ).then(asJson<CoverageResponse>),

  // 202 job accepted. 409 symbol/feed busy surfaces as DataApiError with body
  // { error, active_job_id } — callers attach to the active job instead of failing.
  postLoad: async (body: LoadRequestBody, signal?: AbortSignal): Promise<LoadAcceptedResponse> => {
    const resp = await fetch(`${BASE_URL}/api/data/loads`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
    return asJson<LoadAcceptedResponse>(resp);
  },

  getLoadJob: (jobId: string, signal?: AbortSignal) =>
    fetch(`${BASE_URL}/api/data/loads/${encodeURIComponent(jobId)}`, { signal })
      .then(asJson<LoadJobSnapshotWire>),
```

- [ ] **Step 4: Run tests** — `npm test -- lib/data/coverage.test.ts`, then full `npm test`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend/types/data-tab.ts frontend/lib/services/data-api.ts \
        frontend/lib/data/coverage.ts frontend/lib/data/coverage.test.ts
git commit -m "feat(frontend): coverage/load wire types, dataApi methods, month math helpers"
```

---

### Task 10: FE — archive load form + polled load-job cards on the Data tab

**Files:**
- Create: `frontend/lib/stores/load-jobs-store.ts`
- Create: `frontend/hooks/use-load-job.ts`
- Create: `frontend/components/features/data/load-job-card.tsx`
- Create: `frontend/components/features/data/archive-load-form.tsx`
- Modify: `frontend/lib/stores/data-selection-store.ts` (extend `mode` union with `"load"` + `openLoad()` action)
- Modify: `frontend/components/features/data/data-sidebar.tsx` (render form for `mode === "load"`, title "Load archive data")
- Modify: `frontend/components/features/data/data-tab-root.tsx` (header button + load-job cards section)
- Test: `frontend/components/features/data/archive-load-form.test.tsx`, `frontend/components/features/data/load-job-card.test.tsx`

**Interfaces:**
- Consumes: `dataApi.postLoad/getLoadJob`, types + helpers (Task 9); existing patterns: `Button` (`components/ui/button.tsx`), banner style from `new-aggregate-form.tsx` (`role="alert"` + `border-accent-yellow/50 bg-accent-yellow/10`), polling backoff from `hooks/use-run-status.ts`, zustand-persist shape from `lib/stores/data-jobs-store.ts`.
- Produces: `useLoadJobsStore` — `{ jobs: Record<string, { jobId: string; label: string }>, addJob(jobId, label), removeJob(jobId) }` (persisted, key `"atf-load-jobs"`); `useLoadJob(jobId | null)` — polls until terminal, then invalidates `["data","assets"]`, `["data","exchanges"]` and all `["data","coverage",...]` queries; `<LoadJobCard jobId onDismiss />`; `<ArchiveLoadForm />`. Task 11 and 12 reuse `useLoadJobsStore.addJob` + `LoadJobCard`.

Form design (spec §4: exchange/symbol/feed/range, any Binance symbol):
- exchange: text input, default `"binance"`;
- symbol: text input (uppercased on change) — free text, NOT the catalog combobox, because on-demand loads work for unconfigured symbols;
- asset type: select `spot | perpetual`;
- feed + interval: two selects driven by a local constant mirroring the phase-1 classification table (server-side `not_replenishable`/`invalid_interval` 422s remain the source of truth — surface their `message` in the error banner):

```ts
export const ARCHIVE_FEEDS: ReadonlyArray<{
  feedName: string; label: string; intervals: string[]; assetTypes: string[];
}> = [
  { feedName: "candles", label: "Candles", intervals: ["1m", "5m", "15m", "1h", "4h", "1d"], assetTypes: ["spot", "perpetual"] },
  { feedName: "mark-price", label: "Mark price", intervals: ["1h"], assetTypes: ["perpetual"] },
  { feedName: "open-interest", label: "Open interest", intervals: ["5m"], assetTypes: ["perpetual"] },
  { feedName: "ls-ratio-global", label: "L/S ratio (global)", intervals: ["15m"], assetTypes: ["perpetual"] },
  { feedName: "ls-ratio-top-accounts", label: "L/S ratio (top accounts)", intervals: ["15m"], assetTypes: ["perpetual"] },
  { feedName: "ls-ratio-top-positions", label: "L/S ratio (top positions)", intervals: ["1h"], assetTypes: ["perpetual"] },
];
```

- from/to: `<input type="month">` pair → convert with `loadRangeForMonths(monthsInRange(...))`-style expansion (or directly `from = \`${fromMonth}-01\``, `to = last day of toMonth` via the same helper);
- submit → `postLoad` → 202: `addJob(job_id, \`${symbol} ${feedName}\`)` + toast success; on rejection branch with `err instanceof DataApiError` (exported at `data-api.ts:134`): `err.status === 409` → attach — `addJob((err.body as { active_job_id: string }).active_job_id, ...)` + info toast "Already running — attached"; `err.status === 422` → render `(err.body as { message?: string }).message` in a red error banner; anything else → generic error banner.

`useLoadJob`. Three review-driven requirements baked in: (a) a 404 (registry retention expired) leaves `query.state.data` undefined forever — polling MUST also stop on the ERROR branch or the card polls every 2 s indefinitely; (b) `retry: false` — TanStack's default 3 retries make no sense against a deterministic 404/4xx; (c) invalidation lives in a `useEffect` on the terminal transition, NOT inside `refetchInterval` — the interval callback re-fires on every remount of an already-terminal card and would re-invalidate the world each time. `DataApiError` is already exported from `data-api.ts` (line 134) — branch with `instanceof`:

```ts
import { useEffect } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { DataApiError, dataApi } from "@/lib/services/data-api";
import type { LoadJobSnapshotWire } from "@/types/data-tab";

const isTerminal = (s: string | undefined) => s === "complete" || s === "error";

export function useLoadJob(jobId: string | null) {
  const queryClient = useQueryClient();
  const query = useQuery<LoadJobSnapshotWire>({
    queryKey: ["data", "load-job", jobId],
    queryFn: ({ signal }) => dataApi.getLoadJob(jobId!, signal),
    enabled: !!jobId,
    retry: false,
    refetchInterval: (q) => {
      if (isTerminal(q.state.data?.state)) return false;
      if (q.state.error instanceof DataApiError && q.state.error.status === 404) return false;
      return 2_000;
    },
  });

  const terminalState = isTerminal(query.data?.state) ? query.data?.state : undefined;
  useEffect(() => {
    if (!terminalState) return;
    // Materialized months change the catalog + coverage; refresh both once per completion.
    void queryClient.invalidateQueries({ queryKey: ["data", "assets"] });
    void queryClient.invalidateQueries({ queryKey: ["data", "exchanges"] });
    void queryClient.invalidateQueries({ queryKey: ["data", "coverage"] });
  }, [terminalState, queryClient]);

  return query;
}
```

`LoadJobCard` renders `symbol feed_name interval`, `months_done/months_total` + `current_month` while running, `StatusBadge`-style state chip, `error_message` when failed, and a dismiss button (`removeJob`). 404 from `getLoadJob` (registry retention expired) ⇒ show "expired" and offer dismiss.

`DataTabRoot`: a `"Load archive data"` `Button` (variant `secondary`) in the page header row calls `selection.openLoad()`; below the aggregation "In progress" section render `Object.keys(loadJobs).map(id => <LoadJobCard key={id} jobId={id} ... />)` under the same section heading.

- [ ] **Step 1: Write the failing component tests** — `archive-load-form.test.tsx`: (a) submit posts the exact `LoadRequestBody` (vi.mock `@/lib/services/data-api`; fill inputs; assert `postLoad` called with `{exchange: "binance", symbol: "ETHUSDT", asset_type: "perpetual", feed_name: "open-interest", interval: "5m", from: "2024-01-01", to: "2024-02-29"}`), (b) 409 attaches (mock rejection with `DataApiError`-shaped object carrying `body.active_job_id`; assert store contains that id), (c) 422 shows the server message. `load-job-card.test.tsx`: renders progress fields from a mocked snapshot; terminal state stops polling (fake timers, assert `getLoadJob` call count stabilizes); 404 rejection (mock throws a `DataApiError` with `status: 404`) renders "expired" AND stops polling (call count stabilizes — the regression this hook's `refetchInterval` error branch exists for). Follow the `vi.mock`/`vi.hoisted` idiom in `components/features/data/job-progress.test.tsx`.

- [ ] **Step 2: Run to verify failure** — `npm test -- components/features/data`. Expected: FAIL.

- [ ] **Step 3: Implement** all files above. Wrap tested components in a `QueryClientProvider` in tests (fresh `QueryClient` per test, `retry: false`).

- [ ] **Step 4: Run** — full `npm test`. Expected: PASS (including untouched existing suites).

- [ ] **Step 5: Commit**

```bash
git add frontend/lib/stores/load-jobs-store.ts frontend/hooks/use-load-job.ts \
        frontend/components/features/data/load-job-card.tsx \
        frontend/components/features/data/archive-load-form.tsx \
        frontend/components/features/data/archive-load-form.test.tsx \
        frontend/components/features/data/load-job-card.test.tsx \
        frontend/lib/stores/data-selection-store.ts \
        frontend/components/features/data/data-sidebar.tsx \
        frontend/components/features/data/data-tab-root.tsx
git commit -m "feat(frontend): archive load form + polled load-job cards on Data tab"
```

---

### Task 11: FE — per-feed archive coverage in the feed status panel

**Files:**
- Create: `frontend/components/features/data/coverage-summary.tsx`
- Modify: `frontend/components/features/data/feed-status-card.tsx` (render `<CoverageSummary>` for coverage-bearing feeds)
- Modify: `frontend/components/features/data/data-sidebar.tsx` (pass the full `AssetCatalogEntry` + `FeedCatalogEntry` down — `FeedStatusCard` currently receives only `asset.symbol` string + `feedId`)
- Create: `frontend/lib/data/coverage-mapping.ts` (+ test `coverage-mapping.test.ts`)

**Interfaces:**
- Consumes: `dataApi.getCoverage` (query key `["data", "coverage", exchange, displayName, assetType]`, `staleTime: 30_000`), `findMissingMonths`, `loadRangeForMonths`, `useLoadJobsStore.addJob` (Task 10).
- Produces: `mapCatalogFeedToCoverage(feed: FeedCatalogEntry): { feedName: string; interval: string } | null` — TimeBar `{id: "1h"}` → `{feedName: "candles", interval: "1h"}`; Side feeds → match a coverage entry whose `feed_name === feed.id` OR `` `${feed_name}_${interval}` === feed.id ``; AltBar/Tick → `null` (no month-coverage semantics — aggregated feeds are rebuilt, ticks use `CompleteMonths`, phase 3).

Render inside the status panel, above the JSON editor: `"Archive coverage: 27 months (2024-01 – 2026-06)"`, then when `findMissingMonths(entry.covered_months, firstIso, lastIso)` over the feed's own `[first_timestamp, last_timestamp]` window is non-empty: a yellow `role="alert"` banner `"N archived months missing: 2024-02, 2024-05 …"` with a `Load missing months` button → `postLoad` with `loadRangeForMonths(missing)` and the display-name/type mapping → `addJob` (job card appears via Task 10's section). When `first_timestamp` is null (no status yet), show the covered-month count only — no banner (nothing to anchor a range to; the Data-tab form handles arbitrary ranges).

**Scope contract (deliberate, do not "improve"):** the banner anchors strictly to the feed's own `[first_timestamp, last_timestamp]` window — it surfaces HOLES inside known history. Deep history before `first_timestamp` (pre-listing backfill) is intentionally NOT offered here; that is the archive-load form's job, where the user picks an explicit range. Extending the banner window would re-open the never-clears problem for symbols whose listing date is unknown to the FE.

- [ ] **Step 1: Write the failing tests** — `coverage-mapping.test.ts` pins the three mapping branches; a `coverage-summary.test.tsx` renders with mocked `getCoverage` returning one candles/1h entry with a hole and asserts the banner lists the hole and the button posts the exact range (vi.mock `data-api`).

- [ ] **Step 2: Run to verify failure** — `npm test -- coverage-mapping coverage-summary`. Expected: FAIL.

- [ ] **Step 3: Implement.** `DataSidebar` change: `<FeedStatusCard exchange={exchange} asset={asset} feed={feed} />` with `FeedStatusCardProps` becoming `{ exchange: string; asset: AssetCatalogEntry; feed: FeedCatalogEntry }`; inside, keep using `asset.symbol` for the existing status/aggregate calls and `asset.display_name` + `asset.type` for coverage. Update `feed-status-card.test.tsx` fixtures if present.

- [ ] **Step 4: Run** — full `npm test`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend/components/features/data/coverage-summary.tsx \
        frontend/components/features/data/coverage-summary.test.tsx \
        frontend/components/features/data/feed-status-card.tsx \
        frontend/components/features/data/data-sidebar.tsx \
        frontend/lib/data/coverage-mapping.ts frontend/lib/data/coverage-mapping.test.ts
git commit -m "feat(frontend): archive coverage summary + load-missing action in feed status panel"
```

---

### Task 12: FE — Launch coverage hint (candle feeds only)

**Files:**
- Create: `frontend/components/features/launch/coverage-hint.tsx` (+ test `coverage-hint.test.tsx`)
- Modify: `frontend/components/features/dashboard/run-new-panel.tsx` (track the JSON range in state; render the hint)

**Interfaces:**
- Consumes: `primaries: DataFeedSubscription[]` (RunNewPanel state) — only `kind === "TimeBar"` entries participate (`{ assetName, exchange, timeFrame }`, where `assetName` is the catalog DIRECTORY symbol); the cached `["data","assets"]` catalog to resolve `display_name` + `type`; `dataApi.getCoverage`; `findMissingMonths` / `loadRangeForMonths`; `useLoadJob` + `useLoadJobsStore` (Task 10).
- Produces: `<CoverageHint primaries={DataFeedSubscription[]} startTime={string | null} endTime={string | null} />`.

Spec §4 scope guard: candle feeds only; aux feeds are the Data tab's concern; the hint never blocks launching — it is a warning banner with a button, nothing more.

Behavior per TimeBar primary: resolve the catalog entry (`exchange` + `assetName` → `display_name`, `type`); query coverage; select the entry `feed_name === "candles" && interval === timeFrame`; `missing = findMissingMonths(entry?.covered_months ?? [], startTime, endTime)`. Distinguish two cases: coverage entry absent entirely (feed never materialized — treat every closed month in range as missing) vs present with holes. When `missing.length > 0` render the standard yellow banner:

> `Candles 1h for BTCUSDT: N archived months missing in the selected range (2024-02 … 2024-05)` — **[Load]**

`Load` → `postLoad({ exchange, symbol: display_name, asset_type: type, feed_name: "candles", interval: timeFrame, ...loadRangeForMonths(missing) })` → poll with `useLoadJob`; while running show `months_done/months_total` inline in the banner; on `complete` the coverage invalidation (already inside `useLoadJob`) re-runs the query and the banner clears itself. 409 attaches to the active job id (same pattern as Task 10).

`RunNewPanel` integration:
1. Add state `const [jsonRange, setJsonRange] = useState<{ start: string | null; end: string | null }>({ start: null, end: null });`
2. In `handleDocChange`'s existing `try { JSON.parse(text) }` block (it already parses), read `const bs = obj.backtestSettings as { startTime?: string; endTime?: string } | undefined;` and `setJsonRange({ start: bs?.startTime ?? null, end: bs?.endTime ?? null });` — for ALL modes (optimization included; primaries fan out but the range is shared).
3. Render `<CoverageHint primaries={primaries} startTime={jsonRange.start} endTime={jsonRange.end} />` directly below each `MultiPrimaryPicker` render site (both branches, lines ~664 and ~677), gated on `primaries.length > 0`.

- [ ] **Step 1: Write the failing tests** — `coverage-hint.test.tsx` (vi.mock `data-api`; wrap in `QueryClientProvider`): (a) covered range ⇒ renders nothing; (b) missing months ⇒ banner text contains the count and interval, Load button posts the exact body; (c) subscription whose asset is absent from the catalog ⇒ renders nothing (no crash); (d) non-TimeBar primaries are ignored.

- [ ] **Step 2: Run to verify failure** — `npm test -- coverage-hint`. Expected: FAIL.

- [ ] **Step 3: Implement** the component + the three `run-new-panel.tsx` edits.

- [ ] **Step 4: Run** — full `npm test`. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add frontend/components/features/launch/coverage-hint.tsx \
        frontend/components/features/launch/coverage-hint.test.tsx \
        frontend/components/features/dashboard/run-new-panel.tsx
git commit -m "feat(frontend): Launch candle-coverage hint with one-click archive load"
```

---

### Task 13: Whole-branch verification + live UI validation (controller-level)

**Files:** none created (fixes go to the owning task's files).

- [ ] **Step 1: Full backend build + test sweep (sequential, one dotnet at a time)**

```bash
dotnet build AlgoTradeForge.slnx
dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/
dotnet test tests/AlgoTradeForge.WebApi.Tests/
dotnet test tests/AlgoTradeForge.Domain.Tests/
dotnet test tests/AlgoTradeForge.Application.Tests/
dotnet test tests/AlgoTradeForge.Infrastructure.Tests/
```
Expected: build 0 warnings / 0 errors; all suites green.

- [ ] **Step 2: Frontend sweep** — from `frontend/`: `npm test`. Expected: green.

- [ ] **Step 3: Live UI validation via the `/validate` skill** (launches backend + HistoryLoader + frontend on the VS Code launch ports 5000/5051/3000, test DataRoot `HistoryTest`, drives Playwright MCP). Verify, with screenshots:
  1. Data tab: "Load archive data" button → form → submit a small load (e.g. BTCUSDT perpetual open-interest one closed month) → job card shows `months_done/months_total` progressing → completes → coverage summary for that feed shows the month.
  2. Feed status panel: coverage line + (if a hole exists in the fixture data) the missing-months banner.
  3. Launch panel: pick a candle primary whose HistoryTest coverage has a hole in the selected range → hint banner appears; Load → banner shows progress → clears.
  4. Cross-check `WebApi/appsettings.json` `HistoryLoader:BaseUrl` matches the actual HistoryLoader port in this stack (the repo has a recorded 5050-vs-launch-profile discrepancy) — fix the config if the proxy 502s.
  5. Confirm the policy live: with the stack running, a lazy replenishable feed (`open-interest` — no `Eager` override) gets NO scheduled collection log lines; `candles` (Eager-overridden) and `funding-rate`/`liquidations` (irreplaceable) collect as before.

- [ ] **Step 4: Ledger + docs.** Controller updates `.superpowers/sdd/progress.md` (phase-2 ledger; archive phase-1 ledger as `progress-phase1.md` first) and commits the plan + spec edits under a docs commit.

---

## Self-review checklist (run after drafting, before execution)

- Spec §7.2 coverage: lazy default + Eager override (Tasks 1–2), stream axis (Task 3), Data tab (Tasks 9–11), Launch hint (Task 12). Ledger follow-ups woven in: #1 validator (Task 6), #3 coverage cost (Task 5), #4 composition smoke (Task 7), #6 null-timestamp contract (Task 4). Progress wiring (#5) landed in phase 1's fix wave — Task 10 only consumes it.
- Type consistency: `CollectionPolicy.IsEagerlyCollected` (Tasks 1→2→3); `LoadJobSnapshotWire.state` strings match Task 4's `ToLowerInvariant` projection; `dataApi.getCoverage/postLoad/getLoadJob` names match Tasks 10–12 usage; `display_name`-vs-`symbol` rule stated once in Global notes and repeated at each consumption site.

## Follow-ups (explicitly OUT of this phase)

- Vision doc `docs/service-decomposition-vision.md` §HL@cloud: un-backfillable list shrink + Eager↔cloud-profile linkage (spec Follow-ups; separate docs change hanging off the handover).
- M6 month-rollover replace-guard, M7 candle-ext coverage shadowing — phase 3 alongside the materializer work.
- Per-feed (not per-symbol) orchestrator lock granularity — revisit if long aggTrades jobs bite (spec §5).
- SSE for load jobs (polling is fine at month granularity); replenishable-feed options endpoint to replace the FE `ARCHIVE_FEEDS` constant.
- Phase 3: AggTradesMaterializer (ticks), spot `1s` klines, FundingRateMaterializer, taker-volume via candle-ext.
