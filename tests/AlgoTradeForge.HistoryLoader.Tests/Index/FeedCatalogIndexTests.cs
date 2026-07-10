using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Index;

/// <summary>
/// Catalog list endpoints served from a real <see cref="SqliteHistoryIndex"/> —
/// verifies exchange grouping, feed ordering (time bar before alt bar), and
/// single-asset round-trip.
/// </summary>
public sealed class FeedCatalogIndexTests : IAsyncLifetime, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("atf-feedcat-").FullName;
    private HistoryIndexInitializer _initializer = null!;
    private SqliteHistoryIndex _index = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // manifest_json that declares one candle interval (1h = time bar) plus one alt-bar feed.
    // camelCase matches ManifestJson.Options (no WhenWritingNull on ManifestJson.Options,
    // but absent optional fields deserialize to null/default without error).
    private const string PerpManifest =
        """{"feeds":{"EqV_1h_1000":{"kind":"OHLCV_AltBar","type":{"code":"EqV"},"threshold":{"value":1000.0,"unit":"base_asset","inputMode":"absolute"}}},"candles":{"scaleFactor":100,"intervals":["1h"]}}""";

    public async ValueTask InitializeAsync()
    {
        _initializer = new HistoryIndexInitializer(Path.Combine(_dir, "idx.sqlite"));
        await _initializer.EnsureCreated(Ct);
        _index = new SqliteHistoryIndex(_initializer, _initializer.ConnectionString + ";Pooling=False");
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private FeedCatalog BuildCatalog()
    {
        var opts = Substitute.For<IOptionsMonitor<HistoryLoaderOptions>>();
        opts.CurrentValue.Returns(new HistoryLoaderOptions { DataRoot = _dir });
        return new FeedCatalog(_index, opts, Substitute.For<ISchemaManager>());
    }

    [Fact]
    public async Task GetExchanges_AggregatesCountsPerExchange()
    {
        await _index.UpsertAsset(new("binance", "BTCUSDT_perp", "BTCUSDT", "perpetual", PerpManifest), Ct);
        await _index.UpsertAsset(new("nasdaq", "AAPL", "AAPL", "equity", """{"feeds":{}}"""), Ct);

        var resp = await BuildCatalog().GetExchanges(Ct);

        Assert.Equal(2, resp.Exchanges.Count);
        Assert.Equal("binance", resp.Exchanges[0].Name);   // ordered by name (ordinal)
        Assert.Equal(1, resp.Exchanges[0].AssetCount);
        Assert.Equal("nasdaq", resp.Exchanges[1].Name);
        Assert.Equal(1, resp.Exchanges[1].AssetCount);
    }

    [Fact]
    public async Task GetAllAssets_OrdersFeedsTimeBarBeforeAltBar()
    {
        await _index.UpsertAsset(new("binance", "BTCUSDT_perp", "BTCUSDT", "perpetual", PerpManifest), Ct);
        await _index.UpsertAsset(new("nasdaq", "AAPL", "AAPL", "equity", """{"feeds":{}}"""), Ct);

        var resp = await BuildCatalog().GetAllAssets(Ct);

        Assert.Equal(2, resp.Assets.Count);
        var perp = Assert.Single(resp.Assets, a => a.Symbol == "BTCUSDT_perp");
        Assert.Equal("BTCUSDT-perp", perp.DisplayName);
        Assert.Equal("perpetual", perp.Type);
        Assert.Equal(2, perp.Feeds.Count);
        // Time bar (bucket 1) before alt bar (bucket 2)
        Assert.Equal("OHLCV_TimeBar", perp.Feeds[0].Kind);
        Assert.Equal("1h", perp.Feeds[0].Id);
        Assert.Equal("OHLCV_AltBar", perp.Feeds[1].Kind);
        Assert.Equal("EqV_1h_1000", perp.Feeds[1].Id);
        Assert.Equal("EqV", perp.Feeds[1].TypeCode);
        Assert.Equal(1000m, perp.Feeds[1].ThresholdValue);
    }

    [Fact]
    public async Task GetAsset_RoundTripsFromIndex()
    {
        await _index.UpsertAsset(new("binance", "BTCUSDT_perp", "BTCUSDT", "perpetual", PerpManifest), Ct);

        var entry = await BuildCatalog().GetAsset("binance", "BTCUSDT_perp", Ct);

        Assert.NotNull(entry);
        Assert.Equal("binance", entry!.Exchange);
        Assert.Equal("BTCUSDT_perp", entry.Symbol);
        Assert.Equal("BTCUSDT-perp", entry.DisplayName);
        Assert.Equal("perpetual", entry.Type);
        Assert.Equal(2, entry.Feeds.Count);
    }

    [Fact]
    public async Task GetAsset_ReturnsNull_WhenNotInIndex()
    {
        Assert.Null(await BuildCatalog().GetAsset("binance", "ETHUSDT", Ct));
    }
}
