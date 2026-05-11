using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
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

    private (FeedCatalog catalog, FeedSchemaManager manager, IOptionsMonitor<HistoryLoaderOptions> monitor)
        Build(params AssetCollectionConfig[] assets)
    {
        var options = new HistoryLoaderOptions { DataRoot = _tempDir, Assets = assets.ToList() };
        var monitor = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        monitor.CurrentValue.Returns(options);
        var manager = new FeedSchemaManager();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new FeedCatalog(monitor, manager, cache);
        return (catalog, manager, monitor);
    }

    private static AssetCollectionConfig Asset(string symbol, string type) =>
        new() { Symbol = symbol, Type = type, Exchange = "binance" };

    [Fact]
    public void GetExchanges_ReturnsConfiguredExchangesWithCounts()
    {
        var (catalog, _, _) = Build(
            Asset("BTCUSDT", "spot"),
            Asset("ETHUSDT", "spot"),
            Asset("BTCUSDT", "perpetual"));

        var resp = catalog.GetExchanges();

        Assert.Single(resp.Exchanges);
        Assert.Equal("binance", resp.Exchanges[0].Name);
        Assert.Equal(3, resp.Exchanges[0].AssetCount);
    }

    [Fact]
    public void GetAssetsByExchange_FiltersAndMapsConfiguredAssets()
    {
        var (catalog, _, _) = Build(
            Asset("BTCUSDT", "spot"),
            Asset("ETHUSDT", "spot"));

        var resp = catalog.GetAssetsByExchange("binance");

        Assert.Equal(2, resp.Assets.Count);
        Assert.Contains(resp.Assets, a => a.Symbol == "BTCUSDT" && a.Type == "spot");
    }

    [Fact]
    public void GetAsset_MergesManifestFeedsIntoEntry()
    {
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "perpetual"));
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT_perp");
        // ls-ratio-global has interval=15m on disk (the polling cadence), but it's a
        // Side feed — not a candle. Verifies declared-feed Interval is treated as cadence
        // metadata, not a TimeBar discriminator.
        manager.EnsureSchema(assetDir, "ls-ratio-global", interval: "15m", columns: ["long_pct", "short_pct", "ratio"]);

        var entry = catalog.GetAsset("binance", "BTCUSDT_perp");

        Assert.NotNull(entry);
        Assert.Single(entry!.Feeds);
        var feed = entry.Feeds[0];
        Assert.Equal("ls-ratio-global", feed.Id);
        Assert.Equal("Side", feed.Kind);
        Assert.Equal("15m", feed.Interval);   // cadence preserved as metadata
    }

    [Fact]
    public void GetAsset_FeedsOrdered_TimeBarsBeforeSideFeeds()
    {
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "perpetual"));
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT_perp");
        // Real time bars come from EnsureCandleConfig.
        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m");
        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1h");
        // Side feed declared via EnsureSchema (cadence interval).
        manager.EnsureSchema(assetDir, "funding-rate", interval: "8h", columns: ["rate"]);

        var entry = catalog.GetAsset("binance", "BTCUSDT_perp");

        // Time bars (bucket 1) come first, then Side (bucket 4). Time-bar intra-bucket
        // sort uses CompareOrdinal of intervals: "1h" < "1m" lexically. (FE re-sorts by
        // duration; the BE preserves a stable order across both.)
        Assert.Equal(["1h", "1m", "funding-rate"], entry!.Feeds.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void GetExchanges_CachedAcrossCalls_UntilManifestChanged()
    {
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "spot"));

        var first = catalog.GetExchanges();
        var second = catalog.GetExchanges();
        Assert.Same(first, second);

        // ManifestChanged invalidates the version → next call rebuilds.
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        manager.EnsureSchema(assetDir, "1m", "1m", ["ts", "o", "h", "l", "c", "vol"]);

        var third = catalog.GetExchanges();
        Assert.NotSame(first, third);
    }

    [Fact]
    public void GetFeed_ReturnsNullWhenAssetUnknown()
    {
        var (catalog, _, _) = Build(Asset("BTCUSDT", "spot"));
        Assert.Null(catalog.GetFeed("binance", "DOGEUSDT", "1m"));
    }

    [Fact]
    public void GetFeed_ReturnsNullWhenFeedAbsent()
    {
        var (catalog, _, _) = Build(Asset("BTCUSDT", "spot"));
        Assert.Null(catalog.GetFeed("binance", "BTCUSDT", "EqV_1m_1000"));
    }

    [Fact]
    public void GetFeed_SynthesizesDefinitionForCandleInterval()
    {
        // The /aggregation-options endpoint calls GetFeed to pull a FeedDefinition for the
        // chosen source. Synthesized candle entries (from manifest.Candles.Intervals) must
        // have GetFeed return a non-null definition with Kind=OHLCV_TimeBar so the
        // EligibilityRules check has something to inspect — otherwise the form's Type
        // dropdown stays disabled and the user can't aggregate from a candle.
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "spot"));
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m");

        var def = catalog.GetFeed("binance", "BTCUSDT", "1m");

        Assert.NotNull(def);
        Assert.Equal("OHLCV_TimeBar", def!.Kind);
        Assert.Equal("1m", def.Interval);
    }

    [Fact]
    public void GetAssetsByExchange_DisplayName_DisambiguatesPerpetualFromSpot()
    {
        // Without disambiguation, spot and perpetual rows render with identical row labels
        // ("BTCUSDT" / "BTCUSDT") which the user reads as duplicate rows. The directory-name
        // (Symbol) field already distinguishes via `_perp`; DisplayName needs to as well.
        var (catalog, _, _) = Build(
            Asset("BTCUSDT", "spot"),
            Asset("BTCUSDT", "perpetual"));

        var resp = catalog.GetAssetsByExchange("binance");

        var spot = Assert.Single(resp.Assets, a => a.Type == "spot");
        var perp = Assert.Single(resp.Assets, a => a.Type == "perpetual");
        Assert.Equal("BTCUSDT", spot.DisplayName);
        Assert.Equal("BTCUSDT-perp", perp.DisplayName);
        Assert.NotEqual(spot.DisplayName, perp.DisplayName);
    }

    [Fact]
    public void GetAsset_SynthesizesTimeBarFeedsFromCandleIntervals()
    {
        // Time-bar candles are declared via FeedSchemaManager.EnsureCandleConfig, which
        // populates manifest.Candles.Intervals (separate from manifest.Feeds). The catalog
        // must surface those intervals as OHLCV_TimeBar feed entries so the Data grid shows
        // them as columns and they're available as alt-bar source feeds.
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "spot"));
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m");
        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1h");
        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1d");

        var entry = catalog.GetAsset("binance", "BTCUSDT");

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
    public void GetAsset_FeedWithEmptyIntervalIsClassifiedAsSide_NotTimeBar()
    {
        // The on-disk feeds.json schema serializes side feeds with `"interval": ""` (empty
        // string, not null — the writer at FeedSchemaManager.EnsureSchema:94 stores the
        // raw `interval` arg). A naive `def.Interval is not null` check mis-classifies them
        // as time bars, which then breaks column ordering and downstream eligibility logic.
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "perpetual"));
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT_perp");
        manager.EnsureSchema(assetDir, "funding-rate", interval: "", columns: ["rate"]);

        var entry = catalog.GetAsset("binance", "BTCUSDT_perp");

        var fundingRate = Assert.Single(entry!.Feeds, f => f.Id == "funding-rate");
        Assert.Equal("Side", fundingRate.Kind);
        Assert.Null(fundingRate.Interval);   // normalized away
    }

    [Fact]
    public void GetAsset_FeedNamedTicksWithEmptyIntervalIsClassifiedAsTick()
    {
        // Special-case: the canonical "ticks" feed id maps to Tick kind even when its
        // interval is empty (it's a variable-frequency feed). Without this, all empty-
        // interval feeds collapse to Side and the FE bucket order is wrong (Tick is bucket 3,
        // Side is bucket 4 in the column comparator).
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "perpetual"));
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT_perp");
        manager.EnsureSchema(assetDir, "ticks", interval: "", columns: ["price", "qty"]);

        var entry = catalog.GetAsset("binance", "BTCUSDT_perp");

        var ticks = Assert.Single(entry!.Feeds, f => f.Id == "ticks");
        Assert.Equal("Tick", ticks.Kind);
    }

    [Fact]
    public void GetAsset_DoesNotDuplicateWhenManifestHasBothCandleIntervalAndDeclaredFeed()
    {
        // Defensive: a manifest could (incorrectly) carry an interval id ("1m") in both
        // manifest.Feeds and manifest.Candles.Intervals. The declared feed wins to keep the
        // test deterministic; the synthesized candle entry is skipped so the column never
        // duplicates. The declared-feed kind in this case is "Side" (no explicit Kind, no
        // "ticks" id), but the contract being tested here is just "no duplicate".
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "spot"));
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        manager.EnsureCandleConfig(assetDir, decimalDigits: 2, interval: "1m");
        manager.EnsureSchema(assetDir, "1m", interval: "1m", columns: ["ts", "o", "h", "l", "c", "vol"]);

        var entry = catalog.GetAsset("binance", "BTCUSDT");

        Assert.NotNull(entry);
        Assert.Single(entry!.Feeds, f => f.Id == "1m");   // de-duplicated
    }
}
