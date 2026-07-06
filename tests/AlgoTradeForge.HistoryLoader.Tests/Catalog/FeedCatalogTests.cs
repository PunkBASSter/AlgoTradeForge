using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Catalog;

/// <summary>
/// P1b-20 / P1b-21 / P1b-22 / P1b-23 / P1b-24 — catalog assembly + cache invalidation
/// against a real <see cref="FeedSchemaManager"/>.
/// </summary>
public sealed class FeedCatalogTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"FeedCatalogTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private (FeedCatalog catalog, FeedSchemaManager manager, IOptionsMonitor<HistoryLoaderOptions> monitor)
        Build()
    {
        var options = new HistoryLoaderOptions { DataRoot = _tempDir };
        var monitor = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        monitor.CurrentValue.Returns(options);
        var storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = "" });
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new FeedCatalog(storage, monitor, manager, cache);
        return (catalog, manager, monitor);
    }

    private void WriteManifest(string exchange, string dir)
    {
        var assetDir = Path.Combine(_tempDir, exchange, dir);
        Directory.CreateDirectory(assetDir);
        File.WriteAllText(Path.Combine(assetDir, "feeds.json"), "{}");
    }

    [Fact]
    public async Task GetExchanges_ReturnsOnDiskExchangesWithCounts()
    {
        var (catalog, _, _) = Build();
        WriteManifest("binance", "BTCUSDT");
        WriteManifest("binance", "ETHUSDT");
        WriteManifest("binance", "BTCUSDT_perp");

        var resp = await catalog.GetExchanges(Ct);

        Assert.Single(resp.Exchanges);
        Assert.Equal("binance", resp.Exchanges[0].Name);
        Assert.Equal(3, resp.Exchanges[0].AssetCount);
    }

    [Fact]
    public async Task GetAssetsByExchange_FiltersAndMapsOnDiskAssets()
    {
        var (catalog, _, _) = Build();
        WriteManifest("binance", "BTCUSDT");
        WriteManifest("binance", "ETHUSDT");

        var resp = await catalog.GetAssetsByExchange("binance", Ct);

        Assert.Equal(2, resp.Assets.Count);
        Assert.Contains(resp.Assets, a => a.Symbol == "BTCUSDT" && a.Type == "spot");
    }

    [Fact]
    public async Task GetAsset_MergesManifestFeedsIntoEntry()
    {
        var (catalog, manager, _) = Build();
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT_perp");
        // ls-ratio-global has interval=15m on disk (the polling cadence), but it's a Side
        // feed — not a candle. Verifies declared-feed Interval is treated as cadence metadata,
        // not a TimeBar discriminator.
        await manager.EnsureSchema(assetDir, "ls-ratio-global", interval: "15m", columns: ["long_pct", "short_pct", "ratio"], ct: Ct);

        var entry = await catalog.GetAsset("binance", "BTCUSDT_perp", Ct);

        Assert.NotNull(entry);
        Assert.Single(entry!.Feeds);
        var feed = entry.Feeds[0];
        Assert.Equal("ls-ratio-global", feed.Id);
        Assert.Equal("Side", feed.Kind);
        Assert.Equal("15m", feed.Interval);   // cadence preserved as metadata
    }

    [Fact]
    public async Task GetAsset_FeedsOrdered_TimeBarsBeforeSideFeeds()
    {
        var (catalog, manager, _) = Build();
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT_perp");
        // Real time bars come from EnsureCandleConfig.
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m", Ct);
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1h", Ct);
        // Side feed declared via EnsureSchema (cadence interval).
        await manager.EnsureSchema(assetDir, "funding-rate", interval: "8h", columns: ["rate"], ct: Ct);

        var entry = await catalog.GetAsset("binance", "BTCUSDT_perp", Ct);

        // Time bars (bucket 1) come first, then Side (bucket 4). Time-bar intra-bucket sort
        // uses CompareOrdinal of intervals: "1h" < "1m" lexically. (FE re-sorts by duration;
        // the BE preserves a stable order across both.)
        Assert.Equal(["1h", "1m", "funding-rate"], entry!.Feeds.Select(f => f.Id).ToArray());
    }

    [Fact]
    public async Task GetExchanges_CachedAcrossCalls_UntilManifestChanged()
    {
        var (catalog, manager, _) = Build();

        var first = await catalog.GetExchanges(Ct);
        var second = await catalog.GetExchanges(Ct);
        Assert.Same(first, second);

        // ManifestChanged invalidates the version → next call rebuilds.
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        await manager.EnsureSchema(assetDir, "1m", "1m", ["ts", "o", "h", "l", "c", "vol"], ct: Ct);

        var third = await catalog.GetExchanges(Ct);
        Assert.NotSame(first, third);
    }

    [Fact]
    public async Task GetFeed_ReturnsNullWhenAssetUnknown()
    {
        var (catalog, _, _) = Build();
        Assert.Null(await catalog.GetFeed("binance", "DOGEUSDT", "1m", Ct));
    }

    [Fact]
    public async Task GetFeed_ReturnsNullWhenFeedAbsent()
    {
        var (catalog, _, _) = Build();
        Assert.Null(await catalog.GetFeed("binance", "BTCUSDT", "EqV_1m_1000", Ct));
    }

    [Fact]
    public async Task GetFeed_SynthesizesDefinitionForCandleInterval()
    {
        // The /aggregation-options endpoint calls GetFeed to pull a FeedDefinition for the
        // chosen source. Synthesized candle entries (from manifest.Candles.Intervals) must
        // have GetFeed return a non-null definition with Kind=OHLCV_TimeBar so the
        // EligibilityRules check has something to inspect — otherwise the form's Type
        // dropdown stays disabled and the user can't aggregate from a candle.
        var (catalog, manager, _) = Build();
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m", Ct);

        var def = await catalog.GetFeed("binance", "BTCUSDT", "1m", Ct);

        Assert.NotNull(def);
        Assert.Equal("OHLCV_TimeBar", def!.Kind);
        Assert.Equal("1m", def.Interval);
    }

    [Fact]
    public async Task GetAssetsByExchange_DisplayName_DisambiguatesPerpetualFromSpot()
    {
        // Without disambiguation, spot and perpetual rows render with identical row labels
        // ("BTCUSDT" / "BTCUSDT") which the user reads as duplicate rows. The directory-name
        // (Symbol) field already distinguishes via `_perp`; DisplayName needs to as well.
        var (catalog, _, _) = Build();
        WriteManifest("binance", "BTCUSDT");
        WriteManifest("binance", "BTCUSDT_perp");

        var resp = await catalog.GetAssetsByExchange("binance", Ct);

        var spot = Assert.Single(resp.Assets, a => a.Type == "spot");
        var perp = Assert.Single(resp.Assets, a => a.Type == "perpetual");
        Assert.Equal("BTCUSDT", spot.DisplayName);
        Assert.Equal("BTCUSDT-perp", perp.DisplayName);
        Assert.NotEqual(spot.DisplayName, perp.DisplayName);
    }

    [Fact]
    public async Task GetAsset_SynthesizesTimeBarFeedsFromCandleIntervals()
    {
        // Time-bar candles are declared via FeedSchemaManager.EnsureCandleConfig, which
        // populates manifest.Candles.Intervals (separate from manifest.Feeds). The catalog
        // must surface those intervals as OHLCV_TimeBar feed entries so the Data grid shows
        // them as columns and they're available as alt-bar source feeds.
        var (catalog, manager, _) = Build();
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m", Ct);
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1h", Ct);
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1d", Ct);

        var entry = await catalog.GetAsset("binance", "BTCUSDT", Ct);

        Assert.NotNull(entry);
        var timeBarIds = entry!.Feeds
            .Where(f => f.Kind == "OHLCV_TimeBar")
            .Select(f => f.Id)
            .ToArray();
        // Sorted: "1d" < "1h" < "1m" lexically (FeedOrder uses CompareOrdinal on Interval).
        Assert.Equal(["1d", "1h", "1m"], timeBarIds);
        // Each synthesized entry carries a non-null Interval so the FE comparator places it
        // in the time-bar bucket.
        Assert.All(entry.Feeds.Where(f => f.Kind == "OHLCV_TimeBar"), f =>
        {
            Assert.Equal(f.Id, f.Interval);
            Assert.Null(f.TypeCode);
            Assert.Null(f.ThresholdValue);
        });
    }

    [Fact]
    public async Task GetAsset_FeedWithEmptyIntervalIsClassifiedAsSide_NotTimeBar()
    {
        // The on-disk feeds.json schema serializes side feeds with `"interval": ""` (empty
        // string, not null — the writer at FeedSchemaManager.EnsureSchema stores the raw
        // `interval` arg). A naive `def.Interval is not null` check mis-classifies them as
        // time bars, which then breaks column ordering and downstream eligibility logic.
        var (catalog, manager, _) = Build();
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT_perp");
        await manager.EnsureSchema(assetDir, "funding-rate", interval: "", columns: ["rate"], ct: Ct);

        var entry = await catalog.GetAsset("binance", "BTCUSDT_perp", Ct);

        var fundingRate = Assert.Single(entry!.Feeds, f => f.Id == "funding-rate");
        Assert.Equal("Side", fundingRate.Kind);
        Assert.Null(fundingRate.Interval);   // normalized away
    }

    [Fact]
    public async Task GetAsset_FeedNamedTicksWithEmptyIntervalIsClassifiedAsTick()
    {
        // Special-case: the canonical "ticks" feed id maps to Tick kind even when its
        // interval is empty (it's a variable-frequency feed). Without this, all empty-
        // interval feeds collapse to Side and the FE bucket order is wrong (Tick is bucket 3,
        // Side is bucket 4 in the column comparator).
        var (catalog, manager, _) = Build();
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT_perp");
        await manager.EnsureSchema(assetDir, "ticks", interval: "", columns: ["price", "qty"], ct: Ct);

        var entry = await catalog.GetAsset("binance", "BTCUSDT_perp", Ct);

        var ticks = Assert.Single(entry!.Feeds, f => f.Id == "ticks");
        Assert.Equal("Tick", ticks.Kind);
    }

    [Fact]
    public async Task GetAsset_DoesNotDuplicateWhenManifestHasBothCandleIntervalAndDeclaredFeed()
    {
        // Defensive: a manifest could (incorrectly) carry an interval id ("1m") in both
        // manifest.Feeds and manifest.Candles.Intervals. The declared feed wins to keep the
        // test deterministic; the synthesized candle entry is skipped so the column never
        // duplicates. The declared-feed kind in this case is "Side" (no explicit Kind, no
        // "ticks" id), but the contract being tested here is just "no duplicate".
        var (catalog, manager, _) = Build();
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m", Ct);
        await manager.EnsureSchema(assetDir, "1m", interval: "1m", columns: ["ts", "o", "h", "l", "c", "vol"], ct: Ct);

        var entry = await catalog.GetAsset("binance", "BTCUSDT", Ct);

        Assert.NotNull(entry);
        Assert.Single(entry!.Feeds, f => f.Id == "1m");   // de-duplicated
    }

    [Fact]
    public async Task GetAllAssets_ConcurrentMisses_SingleFlightFactory()
    {
        // Without the per-key SemaphoreSlim gate in CachedAsync, IMemoryCache.GetOrCreate
        // lets N concurrent miss-readers each invoke the factory in parallel; with S3
        // backing IFileStorage that fans out into N×assetCount remote round-trips per
        // cache-miss burst. Verify the gate single-flights.
        var assetDirs = Enumerable.Range(0, 4).Select(i => $"ASSET{i}").ToArray();
        foreach (var dir in assetDirs)
            WriteManifest("binance", dir);

        var options = new HistoryLoaderOptions { DataRoot = _tempDir };
        var monitor = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        monitor.CurrentValue.Returns(options);

        var schema = Substitute.For<ISchemaManager>();
        var loadCount = 0;
        schema.Load(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                Interlocked.Increment(ref loadCount);
                // Hold the factory open long enough for parallel callers to converge on the gate.
                await Task.Delay(50, Ct);
                return (FeedMetadata?)new FeedMetadata();
            });

        var storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = "" });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new FeedCatalog(storage, monitor, schema, cache);

        const int concurrentCallers = 8;
        var tasks = Enumerable.Range(0, concurrentCallers)
            .Select(_ => catalog.GetAllAssets(Ct))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        // All callers must observe the same cached payload (single load won the race).
        Assert.All(results, r => Assert.Same(results[0], r));
        // One Load per asset, not concurrentCallers × asset count.
        Assert.Equal(assetDirs.Length, loadCount);
    }
}
