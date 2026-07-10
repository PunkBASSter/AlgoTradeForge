using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Catalog;

/// <summary>
/// Catalog assembly, feed ordering, and GetFeed behaviour.
/// List/asset tests (P1b-20/21/22/23/24) are now index-seeded fixtures; GetFeed tests
/// (P1b-25–28) still drive a real FeedSchemaManager against the filesystem.
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

    // Build a catalog backed by the supplied index (or an empty substitute for GetFeed tests).
    // DataRoot defaults to _tempDir so GetFeed path-resolution resolves correctly.
    private (FeedCatalog catalog, FeedSchemaManager manager, IOptionsMonitor<HistoryLoaderOptions> monitor)
        Build(IHistoryIndex? index = null, string? dataRoot = null)
    {
        var options = new HistoryLoaderOptions { DataRoot = dataRoot ?? _tempDir };
        var monitor = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        monitor.CurrentValue.Returns(options);
        var manager = new FeedSchemaManager(new LocalFileStorage());
        var catalog = new FeedCatalog(index ?? Substitute.For<IHistoryIndex>(), monitor, manager);
        return (catalog, manager, monitor);
    }

    // Returns a substitute IHistoryIndex that filters ListAssets by exchange (case-insensitive)
    // and resolves GetAsset by (exchange, dir).
    private static IHistoryIndex MakeIndex(params AssetIndexRow[] rows)
    {
        var index = Substitute.For<IHistoryIndex>();
        index.ListAssets(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                string? ex = ci.ArgAt<string?>(0);
                IReadOnlyList<AssetIndexRow> result = ex is null
                    ? rows
                    : rows.Where(r => r.Exchange.Equals(ex, StringComparison.OrdinalIgnoreCase)).ToArray();
                return Task.FromResult(result);
            });
        index.GetAsset(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                string ex  = ci.ArgAt<string>(0);
                string dir = ci.ArgAt<string>(1);
                return Task.FromResult<AssetIndexRow?>(
                    rows.FirstOrDefault(r =>
                        r.Exchange.Equals(ex, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(r.Dir, dir, StringComparison.Ordinal)));
            });
        return index;
    }

    // -------------------------------------------------------------------------
    // Exchange / asset list tests (index-seeded)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetExchanges_ReturnsOnDiskExchangesWithCounts()
    {
        var index = MakeIndex(
            new AssetIndexRow("binance", "BTCUSDT",      "BTCUSDT", "spot",      """{"feeds":{}}"""),
            new AssetIndexRow("binance", "ETHUSDT",      "ETHUSDT", "spot",      """{"feeds":{}}"""),
            new AssetIndexRow("binance", "BTCUSDT_perp", "BTCUSDT", "perpetual", """{"feeds":{}}"""));
        var (catalog, _, _) = Build(index);

        var resp = await catalog.GetExchanges(Ct);

        Assert.Single(resp.Exchanges);
        Assert.Equal("binance", resp.Exchanges[0].Name);
        Assert.Equal(3, resp.Exchanges[0].AssetCount);
    }

    [Fact]
    public async Task GetAssetsByExchange_FiltersAndMapsOnDiskAssets()
    {
        var index = MakeIndex(
            new AssetIndexRow("binance", "BTCUSDT", "BTCUSDT", "spot", """{"feeds":{}}"""),
            new AssetIndexRow("binance", "ETHUSDT", "ETHUSDT", "spot", """{"feeds":{}}"""));
        var (catalog, _, _) = Build(index);

        var resp = await catalog.GetAssetsByExchange("binance", Ct);

        Assert.Equal(2, resp.Assets.Count);
        Assert.Contains(resp.Assets, a => a.Symbol == "BTCUSDT" && a.Type == "spot");
    }

    [Fact]
    public async Task GetAsset_MergesManifestFeedsIntoEntry()
    {
        // ls-ratio-global has interval=15m on disk (the polling cadence), but it's a Side
        // feed — not a candle. Verifies declared-feed Interval is treated as cadence metadata,
        // not a TimeBar discriminator.
        const string manifestJson =
            """{"feeds":{"ls-ratio-global":{"interval":"15m","columns":["long_pct","short_pct","ratio"]}}}""";
        var index = MakeIndex(new AssetIndexRow("binance", "BTCUSDT_perp", "BTCUSDT", "perpetual", manifestJson));
        var (catalog, _, _) = Build(index);

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
        // Real time bars from candles.intervals; Side feed from feeds section.
        const string manifestJson =
            """{"feeds":{"funding-rate":{"interval":"8h","columns":["rate"]}},"candles":{"scaleFactor":100,"intervals":["1m","1h"]}}""";
        var index = MakeIndex(new AssetIndexRow("binance", "BTCUSDT_perp", "BTCUSDT", "perpetual", manifestJson));
        var (catalog, _, _) = Build(index);

        var entry = await catalog.GetAsset("binance", "BTCUSDT_perp", Ct);

        // Time bars (bucket 1) come first, then Side (bucket 4). Time-bar intra-bucket sort
        // uses CompareOrdinal of intervals: "1h" < "1m" lexically. (FE re-sorts by duration;
        // the BE preserves a stable order across both.)
        Assert.Equal(["1h", "1m", "funding-rate"], entry!.Feeds.Select(f => f.Id).ToArray());
    }

    [Fact]
    public async Task GetAssetsByExchange_DisplayName_DisambiguatesPerpetualFromSpot()
    {
        // Without disambiguation, spot and perpetual rows render with identical row labels
        // ("BTCUSDT" / "BTCUSDT") which the user reads as duplicate rows.
        var index = MakeIndex(
            new AssetIndexRow("binance", "BTCUSDT",      "BTCUSDT", "spot",      """{"feeds":{}}"""),
            new AssetIndexRow("binance", "BTCUSDT_perp", "BTCUSDT", "perpetual", """{"feeds":{}}"""));
        var (catalog, _, _) = Build(index);

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
        // Time-bar candles are declared via candles.intervals in the manifest. The catalog
        // must surface those intervals as OHLCV_TimeBar feed entries so the Data grid shows
        // them as columns and they're available as alt-bar source feeds.
        const string manifestJson =
            """{"feeds":{},"candles":{"scaleFactor":100,"intervals":["1m","1h","1d"]}}""";
        var index = MakeIndex(new AssetIndexRow("binance", "BTCUSDT", "BTCUSDT", "spot", manifestJson));
        var (catalog, _, _) = Build(index);

        var entry = await catalog.GetAsset("binance", "BTCUSDT", Ct);

        Assert.NotNull(entry);
        var timeBarIds = entry!.Feeds
            .Where(f => f.Kind == "OHLCV_TimeBar")
            .Select(f => f.Id)
            .ToArray();
        // Sorted: "1d" < "1h" < "1m" lexically (FeedOrder uses CompareOrdinal on Interval).
        Assert.Equal(["1d", "1h", "1m"], timeBarIds);
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
        const string manifestJson = """{"feeds":{"funding-rate":{"interval":"","columns":["rate"]}}}""";
        var index = MakeIndex(new AssetIndexRow("binance", "BTCUSDT_perp", "BTCUSDT", "perpetual", manifestJson));
        var (catalog, _, _) = Build(index);

        var entry = await catalog.GetAsset("binance", "BTCUSDT_perp", Ct);

        var fundingRate = Assert.Single(entry!.Feeds, f => f.Id == "funding-rate");
        Assert.Equal("Side", fundingRate.Kind);
        Assert.Null(fundingRate.Interval);   // normalized away
    }

    [Fact]
    public async Task GetAsset_FeedNamedTicksWithEmptyIntervalIsClassifiedAsTick()
    {
        // Special-case: the canonical "ticks" feed id maps to Tick kind even when its
        // interval is empty (it's a variable-frequency feed).
        const string manifestJson = """{"feeds":{"ticks":{"interval":"","columns":["price","qty"]}}}""";
        var index = MakeIndex(new AssetIndexRow("binance", "BTCUSDT_perp", "BTCUSDT", "perpetual", manifestJson));
        var (catalog, _, _) = Build(index);

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
        // duplicates.
        const string manifestJson =
            """{"feeds":{"1m":{"interval":"1m","columns":["ts","o","h","l","c","vol"]}},"candles":{"scaleFactor":100,"intervals":["1m"]}}""";
        var index = MakeIndex(new AssetIndexRow("binance", "BTCUSDT", "BTCUSDT", "spot", manifestJson));
        var (catalog, _, _) = Build(index);

        var entry = await catalog.GetAsset("binance", "BTCUSDT", Ct);

        Assert.NotNull(entry);
        Assert.Single(entry!.Feeds, f => f.Id == "1m");   // de-duplicated
    }

    // -------------------------------------------------------------------------
    // GetFeed tests — still drive ISchemaManager directly (no index involved)
    // -------------------------------------------------------------------------

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
    public async Task GetFeed_RejectsPathTraversalOutsideAssetDir()
    {
        var (catalog, _, _) = Build();
        // A manifest planted at {DataRoot}/binance/feeds.json (exchange dir, not an asset dir),
        // declaring a "1m" feed — reachable only by traversing out of the asset directory.
        var exchangeDir = Path.Combine(_tempDir, "binance");
        Directory.CreateDirectory(exchangeDir);
        File.WriteAllText(Path.Combine(exchangeDir, "feeds.json"),
            """{ "feeds": { "1m": { "kind": "OHLCV_TimeBar", "interval": "1m" } } }""");

        // "../binance" resolves to {DataRoot}/binance and would otherwise read the planted
        // manifest; the guard must refuse and return null.
        Assert.Null(await catalog.GetFeed("binance", "../binance", "1m", Ct));
        Assert.Null(await catalog.GetFeed("binance", "..", "1m", Ct));
    }

    [Fact]
    public async Task GetFeed_SynthesizesDefinitionForCandleInterval()
    {
        // The /aggregation-options endpoint calls GetFeed to pull a FeedDefinition for the
        // chosen source. Synthesized candle entries (from manifest.Candles.Intervals) must
        // have GetFeed return a non-null definition with Kind=OHLCV_TimeBar so the
        // EligibilityRules check has something to inspect.
        var (catalog, manager, _) = Build();
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        await manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m", Ct);

        var def = await catalog.GetFeed("binance", "BTCUSDT", "1m", Ct);

        Assert.NotNull(def);
        Assert.Equal("OHLCV_TimeBar", def!.Kind);
        Assert.Equal("1m", def.Interval);
    }
}
