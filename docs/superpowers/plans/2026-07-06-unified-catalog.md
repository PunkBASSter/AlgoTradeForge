# Unified filesystem-driven catalog — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the backtest-launch symbol picker (and the Data page) show every asset on disk — crypto, equity, and future paid feeds — sourced from a filesystem scan rather than a config list, searchable, refreshed on demand.

**Architecture:** HistoryLoader's `FeedCatalog` swaps its source from `HistoryLoaderOptions.Assets[]` to a scan of `feeds.json` manifests under its `DataRoot` (one manifest per asset dir). The existing cache/version machinery and `/api/data/*` proxy are unchanged. Asset type (crypto/equity/perp) is classified from exchange name + `_perp` suffix. The frontend replaces the cascading exchange→asset `<select>`s with a searchable combobox over the full catalog. Debug launch configs point at the full `History` root.

**Tech Stack:** C# 14 / .NET 10, ASP.NET Core minimal APIs, `IFileStorage` (AlgoTradeForge.Storage), `IMemoryCache`; TypeScript 5 strict, Next.js 16, TanStack Query, vitest + @testing-library/react.

## Global Constraints

- **One dotnet process at a time.** Never run build/test in parallel — wait for each to finish. Build: `dotnet build AlgoTradeForge.slnx`. Test one project at a time.
- **One type per file**, named after the type.
- **No `Async` suffix** on async methods.
- **`using` over try/finally** for single-release cleanup.
- **Async I/O** interfaces expose `Task`/`Task<T>`/`IAsyncEnumerable<T>` with `CancellationToken ct = default`.
- **Comments terse** — only for non-obvious behavior; no signature restatement.
- **C# tests:** xUnit + NSubstitute; `Assert.Single`/`Assert.Empty` (not `Assert.Equal(1/0, …)`).
- **Frontend:** TypeScript strict, no `any`; wire types stay snake_case (no camelCase converter); tests use vitest (`describe/it/expect/vi`) + `@testing-library/react` + `QueryClientProvider`, mocking `globalThis.fetch`.
- **Windows shell:** `powershell.exe` (no `pwsh`); bash tool available for POSIX.

---

## File Structure

**Create:**
- `src/AlgoTradeForge.HistoryLoader.Domain/AssetDirectoryClassifier.cs` — maps `{exchange, dirName}` → `(symbol, type)`.
- `tests/AlgoTradeForge.HistoryLoader.Tests/Catalog/AssetDirectoryClassifierTests.cs`
- `tests/AlgoTradeForge.HistoryLoader.Tests/Catalog/FeedCatalogScanTests.cs`
- `frontend/components/features/launch/asset-combobox.tsx` — searchable asset picker.
- `frontend/components/features/launch/asset-combobox.test.tsx`

**Modify:**
- `src/AlgoTradeForge.HistoryLoader.Application/Catalog/FeedCatalog.cs` — filesystem scan source + `Refresh()`.
- `src/AlgoTradeForge.HistoryLoader.Application/Catalog/IFeedCatalog.cs` — add `Refresh()`.
- `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CatalogEndpoints.cs` — `POST /api/v1/catalog/refresh`.
- `src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs` — `POST /api/data/refresh` passthrough.
- `src/AlgoTradeForge.WebApi/Data/DataProxyCache.cs` — `InvalidateAllAsync`.
- `src/AlgoTradeForge.Infrastructure/History/FileSystemAssetRepository.cs` — equity reads `decimalDigits` from feeds.json.
- `frontend/components/features/launch/feed-picker.tsx` — use `AssetCombobox` for exchange+asset.
- `frontend/lib/services/data-api.ts` — `refreshCatalog()`.
- `.vscode/launch.json` — default profiles → `History`; add HistoryTest fixture profiles.

---

## Task 1: Asset directory classifier (HistoryLoader.Domain)

**Files:**
- Create: `src/AlgoTradeForge.HistoryLoader.Domain/AssetDirectoryClassifier.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Catalog/AssetDirectoryClassifierTests.cs`

**Interfaces:**
- Produces: `static (string Symbol, string Type) AssetDirectoryClassifier.Classify(string exchange, string dirName)` and `static bool AssetDirectoryClassifier.IsUsEquityExchange(string exchange)`. `Type` is one of the `AssetTypes` constants (`"equity"`, `"perpetual"`, `"spot"`).

- [ ] **Step 1: Write the failing test**

```csharp
using AlgoTradeForge.HistoryLoader.Domain;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Catalog;

public class AssetDirectoryClassifierTests
{
    [Theory]
    [InlineData("NASDAQ", "AAPL", "AAPL", AssetTypes.Equity)]
    [InlineData("NYSE", "SPY", "SPY", AssetTypes.Equity)]
    [InlineData("binance", "BTCUSDT", "BTCUSDT", AssetTypes.Spot)]
    [InlineData("binance", "BTCUSDT_perp", "BTCUSDT", AssetTypes.Perpetual)]
    [InlineData("NASDAQ", "AAPL_perp", "AAPL", AssetTypes.Perpetual)] // _perp wins over equity-exchange
    public void Classify_maps_exchange_and_suffix(string exchange, string dir, string expectedSymbol, string expectedType)
    {
        var (symbol, type) = AssetDirectoryClassifier.Classify(exchange, dir);
        Assert.Equal(expectedSymbol, symbol);
        Assert.Equal(expectedType, type);
    }

    [Fact]
    public void IsUsEquityExchange_is_case_insensitive()
    {
        Assert.True(AssetDirectoryClassifier.IsUsEquityExchange("nasdaq"));
        Assert.False(AssetDirectoryClassifier.IsUsEquityExchange("binance"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter AssetDirectoryClassifierTests`
Expected: FAIL — `AssetDirectoryClassifier` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace AlgoTradeForge.HistoryLoader.Domain;

/// <summary>
/// Classifies an on-disk <c>{exchange}/{dir}</c> asset directory into a raw symbol + type.
/// Lossy by design: a bare dir can't distinguish spot-vs-equity except by exchange, nor
/// <c>_perp</c> as perpetual-vs-future — the catalog only needs a display/filter heuristic;
/// authoritative Asset resolution happens in the main app's FileSystemAssetRepository.
/// </summary>
public static class AssetDirectoryClassifier
{
    private const string PerpSuffix = "_perp";

    private static readonly HashSet<string> UsEquityExchanges =
        new(StringComparer.OrdinalIgnoreCase) { "NASDAQ", "NYSE", "NYSEMKT", "AMEX", "ARCA", "BATS" };

    public static bool IsUsEquityExchange(string exchange) => UsEquityExchanges.Contains(exchange);

    public static (string Symbol, string Type) Classify(string exchange, string dirName)
    {
        if (dirName.EndsWith(PerpSuffix, StringComparison.OrdinalIgnoreCase))
            return (dirName[..^PerpSuffix.Length], AssetTypes.Perpetual);
        if (IsUsEquityExchange(exchange))
            return (dirName, AssetTypes.Equity);
        return (dirName, AssetTypes.Spot);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter AssetDirectoryClassifierTests`
Expected: PASS (6 cases).

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Domain/AssetDirectoryClassifier.cs tests/AlgoTradeForge.HistoryLoader.Tests/Catalog/AssetDirectoryClassifierTests.cs
git commit -m "feat(catalog): asset directory classifier (exchange + _perp -> type)"
```

---

## Task 2: FeedCatalog scans the filesystem instead of config

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Catalog/FeedCatalog.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Catalog/FeedCatalogScanTests.cs`

**Interfaces:**
- Consumes: `AssetDirectoryClassifier.Classify` (Task 1); `IFileStorage.ListKeys(prefix, suffix, recursive, ct)`; `ISchemaManager.Load(assetDir, ct)`; `IOptionsMonitor<HistoryLoaderOptions>.CurrentValue.DataRoot`.
- Produces: `FeedCatalog` now depends on `IFileStorage` (new ctor param, first position). `GetAllAssets`/`GetAssetsByExchange`/`GetExchanges`/`GetFeed` reflect on-disk `feeds.json` manifests. `AssetCatalogEntry.Symbol` = on-disk dir name; `DisplayName` = raw symbol (`{symbol}-perp` for perpetuals); `Type` from the classifier.

- [ ] **Step 1: Write the failing test**

```csharp
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Catalog;

public class FeedCatalogScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atf-catalog-" + Guid.NewGuid().ToString("N"));

    private void WriteManifest(string exchange, string dir)
    {
        var assetDir = Path.Combine(_root, exchange, dir);
        Directory.CreateDirectory(assetDir);
        File.WriteAllText(Path.Combine(assetDir, "feeds.json"), "{\"feeds\":{},\"candles\":{\"scaleFactor\":100,\"intervals\":[\"5m\",\"1d\"]}}");
    }

    private FeedCatalog BuildCatalog()
    {
        WriteManifest("binance", "BTCUSDT");
        WriteManifest("binance", "BTCUSDT_perp");
        WriteManifest("NASDAQ", "AAPL");

        var storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = "" });

        var options = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        options.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _root });

        var schema = Substitute.For<ISchemaManager>();
        schema.Load(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FeedMetadata { Candles = new CandleConfig { ScaleFactor = 100m, Intervals = ["5m", "1d"] } });

        return new FeedCatalog(storage, options, schema, new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task GetAllAssets_lists_every_on_disk_asset_with_classified_type()
    {
        var catalog = BuildCatalog();
        var response = await catalog.GetAllAssets();

        Assert.Equal(3, response.Assets.Count);

        var aapl = Assert.Single(response.Assets, a => a.Symbol == "AAPL");
        Assert.Equal("NASDAQ", aapl.Exchange);
        Assert.Equal(AssetTypes.Equity, aapl.Type);
        Assert.Contains(aapl.Feeds, f => f.Id == "5m");
        Assert.Contains(aapl.Feeds, f => f.Id == "1d");

        var perp = Assert.Single(response.Assets, a => a.Symbol == "BTCUSDT_perp");
        Assert.Equal(AssetTypes.Perpetual, perp.Type);
        Assert.Equal("BTCUSDT-perp", perp.DisplayName);
    }

    [Fact]
    public async Task GetExchanges_counts_assets_per_exchange_from_disk()
    {
        var catalog = BuildCatalog();
        var response = await catalog.GetExchanges();

        var binance = Assert.Single(response.Exchanges, e => e.Name == "binance");
        Assert.Equal(2, binance.AssetCount);
        Assert.Single(response.Exchanges, e => e.Name == "NASDAQ");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FeedCatalogScanTests`
Expected: FAIL — `FeedCatalog` has no ctor taking `IFileStorage` (compile error).

- [ ] **Step 3: Rewrite FeedCatalog to scan the filesystem**

Replace the current `using` block, fields, ctor, `GetExchanges`, `BuildAssetEntries`, `GetFeed`, and remove `ResolveConfiguredAsset`. Add `IFileStorage` and a scan helper. Full replacement of `FeedCatalog.cs` top-to-`BuildAssetEntries` region:

```csharp
using System.Collections.Concurrent;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Storage.Threading;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Catalog;

/// <summary>
/// Filesystem-sourced <see cref="IFeedCatalog"/>: one entry per <c>feeds.json</c> found under
/// <c>DataRoot</c>. Version-suffixed cache keys — <c>ManifestChanged</c> or an explicit
/// <see cref="Refresh"/> bumps the version so new requests rescan. Type is heuristic
/// (see <see cref="AssetDirectoryClassifier"/>).
/// </summary>
public sealed class FeedCatalog : IFeedCatalog
{
    private readonly IFileStorage _storage;
    private readonly IOptionsMonitor<HistoryLoaderOptions> _options;
    private readonly ISchemaManager _schemaManager;
    private readonly IMemoryCache _cache;
    // Refresh-gated (not per-request): a full feeds.json scan touches every file under
    // DataRoot, so hold results until a version bump rather than a short TTL.
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadGates = new();

    private long _version;

    public FeedCatalog(
        IFileStorage storage,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ISchemaManager schemaManager,
        IMemoryCache cache)
    {
        _storage = storage;
        _options = options;
        _schemaManager = schemaManager;
        _cache = cache;

        _schemaManager.ManifestChanged += _ =>
        {
            Interlocked.Increment(ref _version);
            _loadGates.Clear();
        };
    }

    public void Refresh()
    {
        Interlocked.Increment(ref _version);
        _loadGates.Clear();
    }

    public Task<ExchangeListResponse> GetExchanges(CancellationToken ct = default) =>
        CachedAsync($"exchanges:{Version}", async () =>
        {
            var dirs = await ScanAssetDirs(ct);
            var groups = dirs
                .GroupBy(d => d.Exchange, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ExchangeSummary(g.Key, g.Count()))
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToArray();
            return new ExchangeListResponse(groups);
        });

    public Task<AssetListResponse> GetAssetsByExchange(string exchange, CancellationToken ct = default) =>
        CachedAsync($"assets:{exchange}:{Version}", async () =>
            new AssetListResponse(await BuildAssetEntries(exchange, ct)));

    public Task<AssetListResponse> GetAllAssets(CancellationToken ct = default) =>
        CachedAsync($"assets:all:{Version}", async () =>
            new AssetListResponse(await BuildAssetEntries(exchange: null, ct)));

    public async Task<AssetCatalogEntry?> GetAsset(string exchange, string assetSymbol, CancellationToken ct = default)
    {
        var entries = await BuildAssetEntries(exchange, ct);
        return entries.FirstOrDefault(a => string.Equals(a.Symbol, assetSymbol, StringComparison.Ordinal));
    }

    public async Task<FeedDefinition?> GetFeed(string exchange, string assetSymbol, string feedId, CancellationToken ct = default)
    {
        var assetDir = Path.Combine(_options.CurrentValue.DataRoot, exchange, assetSymbol);
        var manifest = await _schemaManager.Load(assetDir, ct);
        if (manifest is null) return null;

        if (manifest.Feeds.TryGetValue(feedId, out var def))
            return def;

        if (manifest.Candles?.Intervals.Contains(feedId) == true)
            return new FeedDefinition { Kind = "OHLCV_TimeBar", Interval = feedId };

        return null;
    }

    // -------------------------------------------------------------------------

    private long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// One (exchange, dir) per <c>feeds.json</c> under DataRoot. feeds.json is the per-asset
    /// marker (both the importer and FeedSchemaManager write exactly one per dir), so scanning
    /// it — rather than candle files — yields ~one key per asset instead of one per partition.
    /// </summary>
    private async Task<List<(string Exchange, string Dir)>> ScanAssetDirs(CancellationToken ct)
    {
        var dataRoot = _options.CurrentValue.DataRoot;
        var seen = new HashSet<(string, string)>();
        var result = new List<(string, string)>();
        await foreach (var key in _storage.ListKeys(dataRoot, suffix: "feeds.json", recursive: true, ct))
        {
            var segments = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3) continue; // …/{exchange}/{dir}/feeds.json
            var exchange = segments[^3];
            var dir = segments[^2];
            if (seen.Add((exchange, dir))) result.Add((exchange, dir));
        }
        result.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }
```

Then replace `BuildAssetEntries` (keep `MapFeed`, `FeedOrder`, `CachedAsync` unchanged) with:

```csharp
    private async Task<AssetCatalogEntry[]> BuildAssetEntries(string? exchange, CancellationToken ct)
    {
        var dataRoot = _options.CurrentValue.DataRoot;
        var dirs = await ScanAssetDirs(ct);
        if (exchange is not null)
            dirs = dirs.Where(d => string.Equals(d.Exchange, exchange, StringComparison.OrdinalIgnoreCase)).ToList();

        var manifests = await Task.WhenAll(dirs.Select(d =>
            _schemaManager.Load(Path.Combine(dataRoot, d.Exchange, d.Dir), ct)));

        var result = new AssetCatalogEntry[dirs.Count];
        for (var i = 0; i < dirs.Count; i++)
        {
            var (exchangeName, dir) = dirs[i];
            var manifest = manifests[i];
            var (symbol, type) = AssetDirectoryClassifier.Classify(exchangeName, dir);

            var declaredFeedDict = manifest?.Feeds ?? new Dictionary<string, FeedDefinition>();
            var declaredFeeds = declaredFeedDict.Select(kvp => MapFeed(kvp.Key, kvp.Value));

            var candleFeeds = (manifest?.Candles?.Intervals ?? [])
                .Where(interval => !declaredFeedDict.ContainsKey(interval))
                .Select(interval => new FeedCatalogEntry(
                    Id: interval, Kind: "OHLCV_TimeBar", Interval: interval,
                    TypeCode: null, ThresholdValue: null, ThresholdUnit: null,
                    FirstBarTs: null, LastBarTs: null, Sidecar: null));

            var feeds = candleFeeds.Concat(declaredFeeds).OrderBy(f => f, FeedOrder.Instance).ToArray();

            result[i] = new AssetCatalogEntry(
                Exchange: exchangeName,
                Symbol: dir,
                DisplayName: AssetTypes.IsFutures(type) ? $"{symbol}-perp" : symbol,
                Type: type,
                Feeds: feeds);
        }
        return result;
    }
```

Note: `CachedAsync` already exists in the file — leave it. Remove the now-unused `AlgoTradeForge.HistoryLoader.Application.Collection` using and the `ResolveConfiguredAsset` method (no longer referenced).

- [ ] **Step 4: Update the DI registration to pass IFileStorage**

`IFileStorage` is already registered (used by `StartupSweepService`), so constructor injection resolves automatically. Confirm `FeedCatalog` registration is unchanged in `src/AlgoTradeForge.HistoryLoader.WebApi/Program.cs` (`AddSingleton<IFeedCatalog, FeedCatalog>()`). No edit expected.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FeedCatalogScanTests`
Expected: PASS (2 tests). If `FeedMetadata`/`CandleConfig` init members differ, match the real definitions in `src/AlgoTradeForge.Domain/History/FeedMetadata.cs`.

- [ ] **Step 6: Full HistoryLoader test suite (guard against regressions in existing FeedCatalog tests)**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/`
Expected: PASS. Any pre-existing config-driven catalog test that asserted config-list behavior must be updated to the filesystem fixture (do not leave it failing — fix or replace it on this branch).

- [ ] **Step 7: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Catalog/FeedCatalog.cs tests/AlgoTradeForge.HistoryLoader.Tests/Catalog/FeedCatalogScanTests.cs
git commit -m "feat(catalog): FeedCatalog sources assets from feeds.json scan, not config"
```

---

## Task 3: Explicit catalog refresh (interface + endpoints)

**Files:**
- Modify: `src/AlgoTradeForge.HistoryLoader.Application/Catalog/IFeedCatalog.cs`
- Modify: `src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CatalogEndpoints.cs`
- Modify: `src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs`
- Modify: `src/AlgoTradeForge.WebApi/Data/DataProxyCache.cs`
- Test: `tests/AlgoTradeForge.HistoryLoader.Tests/Catalog/FeedCatalogScanTests.cs` (add a refresh test)

**Interfaces:**
- Consumes: `FeedCatalog.Refresh()` (Task 2).
- Produces: `IFeedCatalog.Refresh()`; `POST /api/v1/catalog/refresh` (HistoryLoader) → 204; `POST /api/data/refresh` (WebApi) → forwards + drops catalog cache; `DataProxyCache.InvalidateAllAsync(ct)`.

- [ ] **Step 1: Write the failing refresh test** (append to `FeedCatalogScanTests`)

```csharp
    [Fact]
    public async Task Refresh_picks_up_a_newly_added_asset_dir()
    {
        var catalog = BuildCatalog();
        var before = await catalog.GetAllAssets();
        Assert.Equal(3, before.Assets.Count);

        WriteManifest("NYSE", "SPY");
        var stillCached = await catalog.GetAllAssets();
        Assert.Equal(3, stillCached.Assets.Count); // cached at old version

        catalog.Refresh();
        var after = await catalog.GetAllAssets();
        Assert.Equal(4, after.Assets.Count);
        Assert.Single(after.Assets, a => a.Symbol == "SPY");
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FeedCatalogScanTests`
Expected: FAIL — `Refresh` not on `IFeedCatalog` yet is fine (concrete has it from Task 2); this test compiles against the concrete `FeedCatalog` so it should actually PASS already. If it PASSES, good — proceed to expose it on the interface + endpoints below.

- [ ] **Step 3: Add `Refresh()` to the interface**

In `IFeedCatalog.cs`, add inside the interface:

```csharp
    /// <summary>Force the next catalog read to rescan the filesystem.</summary>
    void Refresh();
```

- [ ] **Step 4: Add the HistoryLoader refresh endpoint**

In `CatalogEndpoints.MapCatalogEndpoints`, after the `/assets` GET (line ~20):

```csharp
        v1.MapPost("/catalog/refresh", (IFeedCatalog catalog) =>
        {
            catalog.Refresh();
            return Results.NoContent();
        });
```

- [ ] **Step 5: Add `InvalidateAllAsync` to DataProxyCache**

In `DataProxyCache.cs`, add:

```csharp
    /// <summary>Drops the picker-facing catalog keys after an explicit refresh.</summary>
    public async Task InvalidateAllAsync(CancellationToken ct)
    {
        await cache.RemoveAsync(KeyAllExchanges, ct);
        await cache.RemoveAsync(KeyAllAssets, ct);
    }
```

- [ ] **Step 6: Add the WebApi proxy refresh route**

In `DataEndpoints.MapDataEndpoints`, in the "Mutations" region:

```csharp
        g.MapPost("/refresh",
            async (HttpContext ctx, HistoryLoaderClient client, DataProxyCache cache) =>
            {
                try
                {
                    using var upstream = await client.PostJsonAsync("/api/v1/catalog/refresh",
                        default(JsonElement), ctx.RequestAborted);
                    if ((int)upstream.StatusCode >= 500)
                    {
                        var detail = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted);
                        await DataProxyProblem.UpstreamError((int)upstream.StatusCode, detail).ExecuteAsync(ctx);
                        return;
                    }
                    await cache.InvalidateAllAsync(ctx.RequestAborted);
                    ctx.Response.StatusCode = (int)upstream.StatusCode;
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
```

Confirm `HistoryLoaderClient.PostJsonAsync(path, JsonElement, ct)` exists (used by `PostAggregate`, `DataEndpoints.cs:158`) — it does; reuse it with an empty `default(JsonElement)` body. Ensure `using System.Text.Json;` is present (it is, `DataEndpoints.cs:2`).

- [ ] **Step 7: Build + run the HistoryLoader test suite**

Run: `dotnet build AlgoTradeForge.slnx`
Then: `dotnet test tests/AlgoTradeForge.HistoryLoader.Tests/ --filter FeedCatalogScanTests`
Expected: build PASS (interface + endpoints compile), tests PASS.

- [ ] **Step 8: Manual endpoint smoke (optional but recommended)**

Start HistoryLoader + WebApi (see Task 5 for the launch profile), then:
```bash
curl -s -X POST http://localhost:5000/api/data/refresh -i | head -1   # expect 204
curl -s http://localhost:5000/api/data/assets | head -c 300           # expect equity + crypto symbols
```

- [ ] **Step 9: Commit**

```bash
git add src/AlgoTradeForge.HistoryLoader.Application/Catalog/IFeedCatalog.cs src/AlgoTradeForge.HistoryLoader.WebApi/Endpoints/CatalogEndpoints.cs src/AlgoTradeForge.WebApi/Endpoints/DataEndpoints.cs src/AlgoTradeForge.WebApi/Data/DataProxyCache.cs tests/AlgoTradeForge.HistoryLoader.Tests/Catalog/FeedCatalogScanTests.cs
git commit -m "feat(catalog): explicit refresh endpoint (POST /api/data/refresh)"
```

---

## Task 4: Equity reads decimalDigits from feeds.json (scale correctness)

**Files:**
- Modify: `src/AlgoTradeForge.Infrastructure/History/FileSystemAssetRepository.cs:79-87`
- Test: `tests/AlgoTradeForge.Infrastructure.Tests/History/FileSystemAssetRepositoryTests.cs` (create if absent)

**Interfaces:**
- Consumes: existing `ReadDecimalDigitsFromFeedsJson` (already computed at line 77 for every asset), `EquityAsset`.
- Produces: `EquityAsset` whose `TickSize` reflects the manifest scaleFactor (`10^-decimalDigits`) instead of the hardcoded `0.01`.

- [ ] **Step 1: Write the failing test**

```csharp
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.Assets;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class FileSystemAssetRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atf-assets-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Equity_tick_size_comes_from_feeds_json_scale_factor()
    {
        // scaleFactor 1000 => 3 decimals => tick 0.001
        var dir = Path.Combine(_root, "NASDAQ", "AAPL");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "feeds.json"),
            "{\"feeds\":{},\"candles\":{\"scaleFactor\":1000,\"intervals\":[\"1d\"]}}");

        var provider = Substitute.For<IAvailableAssetsProvider>();
        provider.GetAvailableAssets(Arg.Any<CancellationToken>())
            .Returns([new AvailableAssetInfo("NASDAQ", "AAPL", IsFutures: false)]);

        var options = Options.Create(new CandleStorageOptions { DataRoot = _root });
        var repo = new FileSystemAssetRepository(
            new LocalFileStorage(new LocalStorageOptions { DataRoot = "" }),
            provider, options, NullLogger<FileSystemAssetRepository>.Instance);

        var asset = await repo.GetByNameAsync("AAPL", "NASDAQ");
        var equity = Assert.IsType<EquityAsset>(asset);
        Assert.Equal(0.001m, equity.TickSize);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

Note: `AAPL|NASDAQ` is a hardcoded seed (`SeedHardcodedAssets`, line 110) that takes precedence — use a symbol NOT seeded, e.g. change the test to `TSLA` to bypass the seed. Update both the manifest dir and provider/GetByName to `TSLA`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/ --filter FileSystemAssetRepositoryTests`
Expected: FAIL — `TickSize` is `0.01` (base default), not `0.001`.

- [ ] **Step 3: Implement — pass decimalDigits into the equity branch**

In `FileSystemAssetRepository.BuildAssetDictionary`, replace the equity switch arm (line 85):

```csharp
                _ when IsUsEquityExchange(info.Exchange) =>
                    new EquityAsset
                    {
                        Name = info.Symbol,
                        Exchange = info.Exchange,
                        TickSize = decimalDigits > 0 ? 1m / (decimal)Math.Pow(10, decimalDigits) : 0.01m,
                    },
```

Confirm `EquityAsset.TickSize` has an accessible `init`/`set` (it derives from `Asset` where `TickSize` default is `0.01m`). If `TickSize` is get-only on `EquityAsset`, set it via the same object-initializer path the hardcoded seeds do not use — check `src/AlgoTradeForge.Domain/Assets/EquityAsset.cs` and `Asset.cs`. If it is get-only, add an `init` accessor to the `Asset.TickSize` property (it is already assigned via initializer for `FutureAsset` at line 112, so an `init` setter exists).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/AlgoTradeForge.Infrastructure.Tests/ --filter FileSystemAssetRepositoryTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/AlgoTradeForge.Infrastructure/History/FileSystemAssetRepository.cs tests/AlgoTradeForge.Infrastructure.Tests/History/FileSystemAssetRepositoryTests.cs
git commit -m "fix(assets): equity tick size from feeds.json scaleFactor, not hardcoded 0.01"
```

---

## Task 5: Point debug launch configs at the full `History` root

**Files:**
- Modify: `.vscode/launch.json`

**Interfaces:** none (config).

- [ ] **Step 1: Repoint the default WebApi + HistoryLoader profiles to `History`**

In `.vscode/launch.json`, change the three `HistoryTest` occurrences (lines 15, 36, 50) to `History`:
- `"Backend: WebApi"` → `"CandleStorage__DataRoot": "C:\\Users\\Andrew\\AppData\\Local\\AlgoTradeForge\\History"`
- `"Backend: WebApi (with log)"` → same value at line 36
- `"HistoryLoader: WebApi"` → `"HistoryLoader__DataRoot": "C:\\Users\\Andrew\\AppData\\Local\\AlgoTradeForge\\History"`

- [ ] **Step 2: Add fast-fixture profiles that keep `HistoryTest`**

Add two configurations to the `configurations` array (crypto-only fast iteration):

```json
        {
            "name": "Backend: WebApi (fixture)",
            "type": "coreclr",
            "request": "launch",
            "program": "${workspaceFolder}/src/AlgoTradeForge.WebApi/bin/Debug/net10.0/AlgoTradeForge.WebApi.dll",
            "args": [],
            "cwd": "${workspaceFolder}/src/AlgoTradeForge.WebApi",
            "env": {
                "ASPNETCORE_ENVIRONMENT": "Development",
                "ASPNETCORE_URLS": "http://localhost:5000",
                "HistoryLoader__BaseUrl": "http://localhost:5051",
                "CandleStorage__DataRoot": "C:\\Users\\Andrew\\AppData\\Local\\AlgoTradeForge\\HistoryTest"
            },
            "preLaunchTask": "build-backend"
        },
        {
            "name": "HistoryLoader: WebApi (fixture)",
            "type": "coreclr",
            "request": "launch",
            "program": "${workspaceFolder}/src/AlgoTradeForge.HistoryLoader.WebApi/bin/Debug/net10.0/AlgoTradeForge.HistoryLoader.WebApi.dll",
            "args": [],
            "cwd": "${workspaceFolder}/src/AlgoTradeForge.HistoryLoader.WebApi",
            "env": {
                "ASPNETCORE_ENVIRONMENT": "Development",
                "ASPNETCORE_URLS": "http://localhost:5051",
                "HistoryLoader__DataRoot": "C:\\Users\\Andrew\\AppData\\Local\\AlgoTradeForge\\HistoryTest"
            },
            "preLaunchTask": "build-history-loader"
        }
```

- [ ] **Step 3: Add a compound for the fixture stack** (append to `compounds`)

```json
        {
            "name": "Full Stack: Backend + Frontend (fixture)",
            "configurations": ["HistoryLoader: WebApi (fixture)", "Backend: WebApi (fixture)", "Frontend: npm run dev"],
            "stopAll": true
        }
```

- [ ] **Step 4: Verify JSON validity**

Run: `node -e "JSON.parse(require('fs').readFileSync('.vscode/launch.json','utf8')); console.log('ok')"`
Expected: `ok`.

- [ ] **Step 5: Manual verification**

Launch **"Full Stack: Backend + Frontend"**, open the backtest launch panel, confirm NASDAQ/NYSE equity symbols appear alongside crypto in the picker. (This is the end-to-end proof of Problem 1's fix.)

- [ ] **Step 6: Commit**

```bash
git add .vscode/launch.json
git commit -m "chore(debug): default launch reads full History root; keep HistoryTest fixture profiles"
```

---

## Task 6: Searchable asset combobox (frontend)

**Files:**
- Create: `frontend/components/features/launch/asset-combobox.tsx`
- Create: `frontend/components/features/launch/asset-combobox.test.tsx`
- Modify: `frontend/lib/services/data-api.ts`
- Modify: `frontend/components/features/launch/feed-picker.tsx`

**Interfaces:**
- Consumes: `dataApi.getAssets` (existing, returns `AssetListResponse` with full `AssetCatalogEntry[]`); `AssetCatalogEntry` type.
- Produces: `AssetCombobox` component (`{ value, onSelect, disabled }`), `dataApi.refreshCatalog()`. `FeedPicker` uses `AssetCombobox` in place of the Exchange + Asset `<select>`s; the Feed `<select>` is unchanged.

- [ ] **Step 1: Add the API client functions**

In `frontend/lib/services/data-api.ts`, inside the `dataApi` object (after `getAssets`, line 57):

```ts
  refreshCatalog: async (signal?: AbortSignal): Promise<void> => {
    const resp = await fetch(`${BASE_URL}/api/data/refresh`, { method: "POST", signal });
    if (!resp.ok) await asJson(resp);
  },
```

- [ ] **Step 2: Write the failing component test**

`frontend/components/features/launch/asset-combobox.test.tsx`:

```tsx
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { AssetCombobox } from "./asset-combobox";
import type { AssetCatalogEntry } from "@/types/data-tab";

const fetchMock = vi.fn();
beforeEach(() => {
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  fetchMock.mockReset();
});

const entry = (exchange: string, symbol: string, type: string): AssetCatalogEntry => ({
  exchange, symbol, display_name: symbol, type, feeds: [],
});

function mockAssets(assets: AssetCatalogEntry[]) {
  fetchMock.mockResolvedValue(new Response(JSON.stringify({ assets }), { status: 200 }));
}

function renderCombobox(onSelect = vi.fn()) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <AssetCombobox value={null} onSelect={onSelect} />
    </QueryClientProvider>,
  );
  return onSelect;
}

describe("AssetCombobox", () => {
  it("filters the catalog by the typed query across symbol and exchange", async () => {
    mockAssets([
      entry("NASDAQ", "AAPL", "equity"),
      entry("NASDAQ", "MSFT", "equity"),
      entry("binance", "BTCUSDT", "spot"),
    ]);
    renderCombobox();

    const input = await screen.findByRole("combobox", { name: /asset/i });
    fireEvent.change(input, { target: { value: "aapl" } });

    await waitFor(() => expect(screen.getByText("AAPL")).toBeInTheDocument());
    expect(screen.queryByText("BTCUSDT")).not.toBeInTheDocument();
  });

  it("emits the chosen entry on click", async () => {
    mockAssets([entry("binance", "BTCUSDT", "spot")]);
    const onSelect = renderCombobox();

    const input = await screen.findByRole("combobox", { name: /asset/i });
    fireEvent.change(input, { target: { value: "btc" } });
    fireEvent.click(await screen.findByText("BTCUSDT"));

    expect(onSelect).toHaveBeenCalledWith(
      expect.objectContaining({ exchange: "binance", symbol: "BTCUSDT" }),
    );
  });
});
```

- [ ] **Step 3: Run to verify it fails**

Run: `cd frontend && npx vitest run components/features/launch/asset-combobox.test.tsx`
Expected: FAIL — `asset-combobox` module not found.

- [ ] **Step 4: Implement the component**

`frontend/components/features/launch/asset-combobox.tsx`:

```tsx
"use client";

// Searchable single-select over the FULL on-disk catalog (crypto + equity + paid feeds).
// Replaces the exchange→asset cascade: one text query filters across symbol + exchange +
// type. The catalog is fetched once and cached (refresh via the Data page / refreshCatalog).

import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { dataApi } from "@/lib/services/data-api";
import type { AssetCatalogEntry } from "@/types/data-tab";

interface AssetComboboxProps {
  value: { exchange: string; symbol: string } | null;
  onSelect: (entry: AssetCatalogEntry) => void;
  disabled?: boolean;
}

const MAX_RESULTS = 50;

const INPUT_CLASSES =
  "w-full rounded-md border border-border-default bg-bg-base px-2 py-1.5 text-sm text-text-primary " +
  "focus:border-accent-blue focus:outline-none focus:ring-1 focus:ring-accent-blue " +
  "disabled:opacity-50 disabled:cursor-not-allowed";

export function AssetCombobox({ value, onSelect, disabled }: AssetComboboxProps) {
  const [query, setQuery] = useState("");
  const [open, setOpen] = useState(false);

  const assetsQuery = useQuery({
    queryKey: ["data", "assets"],
    queryFn: ({ signal }) => dataApi.getAssets(signal),
    staleTime: Infinity,
  });

  const matches = useMemo(() => {
    const all = assetsQuery.data?.assets ?? [];
    const q = query.trim().toLowerCase();
    if (!q) return all.slice(0, MAX_RESULTS);
    return all
      .filter(
        (a) =>
          a.display_name.toLowerCase().includes(q) ||
          a.symbol.toLowerCase().includes(q) ||
          a.exchange.toLowerCase().includes(q) ||
          a.type.toLowerCase().includes(q),
      )
      .slice(0, MAX_RESULTS);
  }, [assetsQuery.data, query]);

  const selectedLabel = value ? `${value.symbol} · ${value.exchange}` : "";

  return (
    <div className="relative">
      <label className="block text-xs font-medium uppercase tracking-wider text-text-muted mb-1">
        Asset
      </label>
      <input
        role="combobox"
        aria-expanded={open}
        aria-label="Asset"
        className={INPUT_CLASSES}
        placeholder={assetsQuery.isLoading ? "Loading catalog…" : "Search symbol or exchange…"}
        value={open ? query : selectedLabel}
        disabled={disabled || assetsQuery.isLoading}
        onFocus={() => setOpen(true)}
        onChange={(e) => {
          setQuery(e.target.value);
          setOpen(true);
        }}
        onBlur={() => setTimeout(() => setOpen(false), 120)}
      />
      {open && matches.length > 0 && (
        <ul
          className="absolute z-10 mt-1 max-h-64 w-full overflow-auto rounded-md border border-border-default bg-bg-panel shadow-lg"
          role="listbox"
        >
          {matches.map((a) => (
            <li key={`${a.exchange}|${a.symbol}`} role="option" aria-selected={false}>
              <button
                type="button"
                className="flex w-full items-center justify-between gap-2 px-2 py-1.5 text-left text-sm text-text-primary hover:bg-bg-base"
                onMouseDown={(e) => e.preventDefault()}
                onClick={() => {
                  onSelect(a);
                  setQuery("");
                  setOpen(false);
                }}
              >
                <span className="font-medium">{a.display_name}</span>
                <span className="text-xs text-text-muted">
                  {a.exchange} · {a.type}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
```

- [ ] **Step 5: Run to verify it passes**

Run: `cd frontend && npx vitest run components/features/launch/asset-combobox.test.tsx`
Expected: PASS (2 tests).

- [ ] **Step 6: Wire AssetCombobox into FeedPicker**

In `frontend/components/features/launch/feed-picker.tsx`:
1. Add import: `import { AssetCombobox } from "./asset-combobox";`
2. Remove `exchangesQuery` and `assetsQuery` (lines 52-61) and the `selectedAsset` derivation that reads `assetsQuery.data` (lines 63-66). Replace `selectedAsset` with component state holding the chosen entry:

```tsx
  const [selectedAsset, setSelectedAsset] = useState<AssetCatalogEntry | null>(null);
```
(add `useState` to the React import).

3. Replace the Exchange `<div>` and Asset `<div>` blocks (lines 142-189) with:

```tsx
      <div className="sm:col-span-2">
        <AssetCombobox
          value={value?.exchange && value?.asset ? { exchange: value.exchange, symbol: value.asset } : null}
          disabled={disabled}
          onSelect={(entry) => {
            setSelectedAsset(entry);
            onChange({ exchange: entry.exchange, asset: entry.symbol, feedId: "", subscription: null });
          }}
        />
      </div>
```

4. The Feed `<select>` block (lines 191-216) stays as-is; it already reads `selectedAsset.feeds` and `eligibleFeeds`. Keep the `grid grid-cols-1 sm:grid-cols-3` wrapper so the combobox spans two columns and the feed select one.

- [ ] **Step 7: Typecheck + run the launch test suite**

Run: `cd frontend && npx tsc --noEmit`
Expected: no errors (strict). Then: `cd frontend && npx vitest run components/features/launch/`
Expected: PASS. Fix any broken FeedPicker consumer tests (e.g. `multi-primary-picker` tests) by driving the new combobox instead of the removed selects — do not leave them failing.

- [ ] **Step 8: Manual verification**

With the full stack running (Task 5), open the backtest launch panel → "+ Add primary" → type `AAPL` in the Asset box → select it → pick the `1d` (or `5m`) feed. Confirm the chip is added and equity is selectable.

- [ ] **Step 9: Commit**

```bash
git add frontend/components/features/launch/asset-combobox.tsx frontend/components/features/launch/asset-combobox.test.tsx frontend/lib/services/data-api.ts frontend/components/features/launch/feed-picker.tsx
git commit -m "feat(launch): searchable asset combobox over the full catalog"
```

---

## Self-review notes

- **Spec coverage:** #1 fast scan → Task 2 (feeds.json scan; the "shallow" perf goal is met by manifest-keying, not directory-walking — documented as refresh-gated). #2 refresh → Task 3. #3 type classification → Task 1. #4 slim list → **intentionally dropped** (equity entries are tiny; full `/assets` payload is small — see plan intro; revisit only if payload grows). #5 searchable picker → Task 6. #6 debug roots → Task 5. #7 equity scale → Task 4.
- **Deviation flagged for the user:** slim-list DTO omitted (Task 6 uses the existing full `/assets`). Confirm this is acceptable.
- **Out of scope (unchanged):** Problem 2 (D1+M5 timeframe exception) and the `MultiPrimaryPicker` `maxPrimaries={1}` cap.
- **Ordering:** Tasks 1→2→3 are sequential (each builds on the prior). Task 4 and Task 5 are independent. Task 6 depends on Task 2 (catalog returns equity) and Task 5 (debug reads History) for its manual verification, but its unit test mocks `fetch` so it can be implemented anytime after Task 1's types exist.
