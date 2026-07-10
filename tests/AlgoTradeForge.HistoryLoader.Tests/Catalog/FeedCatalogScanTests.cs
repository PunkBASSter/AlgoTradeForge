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
    private ISchemaManager _schema = null!;

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

        _schema = Substitute.For<ISchemaManager>();
        _schema.Load(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new FeedMetadata { Candles = new CandleConfig { ScaleFactor = 100m, Intervals = ["5m", "1d"] } });

        return new FeedCatalog(storage, options, _schema, new MemoryCache(new MemoryCacheOptions()));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAllAssets_lists_every_on_disk_asset_with_classified_type()
    {
        var catalog = BuildCatalog();
        var response = await catalog.GetAllAssets(Ct);

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
        var response = await catalog.GetExchanges(Ct);

        var binance = Assert.Single(response.Exchanges, e => e.Name == "binance");
        Assert.Equal(2, binance.AssetCount);
        Assert.Single(response.Exchanges, e => e.Name == "NASDAQ");
    }

    [Fact]
    public async Task ManifestChanged_picks_up_a_newly_added_asset_dir()
    {
        // Refresh() was removed from IFeedCatalog; cache invalidation now happens only via
        // ManifestChanged event (or 10-min TTL). Simulate an out-of-band manifest write.
        var catalog = BuildCatalog();
        var before = await catalog.GetAllAssets(Ct);
        Assert.Equal(3, before.Assets.Count);

        WriteManifest("NYSE", "SPY");
        var stillCached = await catalog.GetAllAssets(Ct);
        Assert.Equal(3, stillCached.Assets.Count); // cached at old version

        _schema.ManifestChanged += Raise.Event<Action<string>>("/any");
        var after = await catalog.GetAllAssets(Ct);
        Assert.Equal(4, after.Assets.Count);
        Assert.Single(after.Assets, a => a.Symbol == "SPY");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
