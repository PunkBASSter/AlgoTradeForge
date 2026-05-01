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
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "spot"));
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        manager.EnsureSchema(assetDir, "1m", interval: "1m", columns: ["ts", "o", "h", "l", "c", "vol"]);

        var entry = catalog.GetAsset("binance", "BTCUSDT");

        Assert.NotNull(entry);
        Assert.Single(entry!.Feeds);
        var feed = entry.Feeds[0];
        Assert.Equal("1m", feed.Id);
        Assert.Equal("OHLCV_TimeBar", feed.Kind);   // derived from non-null Interval
        Assert.Equal("1m", feed.Interval);
    }

    [Fact]
    public void GetAsset_FeedsOrdered_TimeBarsBeforeAltBars()
    {
        var (catalog, manager, _) = Build(Asset("BTCUSDT", "spot"));
        var assetDir = Path.Combine(_tempDir, "binance", "BTCUSDT");
        manager.EnsureSchema(assetDir, "5m", "5m", ["ts", "o", "h", "l", "c", "vol"]);
        manager.EnsureSchema(assetDir, "1m", "1m", ["ts", "o", "h", "l", "c", "vol"]);

        var entry = catalog.GetAsset("binance", "BTCUSDT");

        // Time bars sorted by interval lex: "1m" < "5m"
        Assert.Equal(["1m", "5m"], entry!.Feeds.Select(f => f.Id).ToArray());
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
}
