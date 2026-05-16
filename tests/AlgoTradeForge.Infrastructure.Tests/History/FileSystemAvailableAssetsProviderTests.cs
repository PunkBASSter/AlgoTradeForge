using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.Infrastructure.IO;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class FileSystemAvailableAssetsProviderTests : IDisposable
{
    private readonly string _testDataRoot;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FileSystemAvailableAssetsProviderTests()
    {
        _testDataRoot = Path.Combine(Path.GetTempPath(), $"AvailAssets_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataRoot))
            Directory.Delete(_testDataRoot, recursive: true);
    }

    private FileSystemAvailableAssetsProvider CreateProvider()
    {
        var storage = new LocalFileStorage();
        var opts = Options.Create(new CandleStorageOptions { DataRoot = _testDataRoot });
        return new FileSystemAvailableAssetsProvider(storage, opts);
    }

    private void WriteCandleFile(string exchange, string assetDir, string subDir, string fileName)
    {
        var dir = Path.Combine(_testDataRoot, exchange, assetDir, subDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), "ts,o,h,l,c,vol\n");
    }

    [Fact]
    public async Task GetAvailableAssets_NewFormat_DiscoversAsset()
    {
        WriteCandleFile("Binance", "BTCUSDT", "candles", "2024-01_1m.csv");

        var assets = await CreateProvider().GetAvailableAssets(Ct);

        var info = Assert.Single(assets);
        Assert.Equal("Binance", info.Exchange);
        Assert.Equal("BTCUSDT", info.Symbol);
        Assert.False(info.IsFutures);
    }

    [Fact]
    public async Task GetAvailableAssets_LegacyYearFormat_DiscoversAsset()
    {
        WriteCandleFile("Binance", "ETHUSDT", "2024", "2024-01.csv");

        var assets = await CreateProvider().GetAvailableAssets(Ct);

        var info = Assert.Single(assets);
        Assert.Equal("ETHUSDT", info.Symbol);
        Assert.False(info.IsFutures);
    }

    [Fact]
    public async Task GetAvailableAssets_PerpSuffix_StrippedAndFlagged()
    {
        WriteCandleFile("Binance", "BTCUSDT_perp", "candles", "2024-01_1m.csv");

        var assets = await CreateProvider().GetAvailableAssets(Ct);

        var info = Assert.Single(assets);
        Assert.Equal("BTCUSDT", info.Symbol);
        Assert.True(info.IsFutures);
    }

    [Fact]
    public async Task GetAvailableAssets_DeduplicatesAcrossMultipleFiles()
    {
        WriteCandleFile("Binance", "BTCUSDT", "candles", "2024-01_1m.csv");
        WriteCandleFile("Binance", "BTCUSDT", "candles", "2024-02_1m.csv");
        WriteCandleFile("Binance", "BTCUSDT", "candles", "2024-01_5m.csv");

        var assets = await CreateProvider().GetAvailableAssets(Ct);

        Assert.Single(assets);
    }

    [Fact]
    public async Task GetAvailableAssets_IgnoresSideFeedDirectories()
    {
        // Side feeds like funding_rate, oi, ratios sit under exchange/asset/<feedName>/<file>.csv
        // — third segment is neither "candles" nor a 4-digit year, so they must not register
        // an asset by themselves.
        WriteCandleFile("Binance", "BTCUSDT_perp", "funding_rate", "2024-01_8h.csv");

        var assets = await CreateProvider().GetAvailableAssets(Ct);

        Assert.Empty(assets);
    }

    [Fact]
    public async Task GetAvailableAssets_SortsByExchangeThenSymbol()
    {
        WriteCandleFile("Coinbase", "BTCUSD", "candles", "2024-01_1m.csv");
        WriteCandleFile("Binance", "ETHUSDT", "candles", "2024-01_1m.csv");
        WriteCandleFile("Binance", "BTCUSDT", "candles", "2024-01_1m.csv");

        var assets = await CreateProvider().GetAvailableAssets(Ct);

        Assert.Equal(3, assets.Count);
        Assert.Equal(("Binance", "BTCUSDT"), (assets[0].Exchange, assets[0].Symbol));
        Assert.Equal(("Binance", "ETHUSDT"), (assets[1].Exchange, assets[1].Symbol));
        Assert.Equal(("Coinbase", "BTCUSD"), (assets[2].Exchange, assets[2].Symbol));
    }

    [Fact]
    public async Task GetAvailableAssets_EmptyDataRoot_ReturnsEmpty()
    {
        var assets = await CreateProvider().GetAvailableAssets(Ct);
        Assert.Empty(assets);
    }

    [Fact]
    public async Task GetAvailableAssets_IsMemoized_SecondCallNoIO()
    {
        WriteCandleFile("Binance", "BTCUSDT", "candles", "2024-01_1m.csv");
        var provider = CreateProvider();
        var first = await provider.GetAvailableAssets(Ct);

        // Add a new asset after the first call — memoization means it should NOT appear.
        WriteCandleFile("Binance", "ETHUSDT", "candles", "2024-01_1m.csv");

        var second = await provider.GetAvailableAssets(Ct);
        Assert.Same(first, second);
    }
}
