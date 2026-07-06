using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.Infrastructure.History;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class StorageAssetRepositoryTests : IDisposable
{
    private readonly string _testDataRoot;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public StorageAssetRepositoryTests()
    {
        _testDataRoot = Path.Combine(Path.GetTempPath(), $"AssetRepo_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDataRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDataRoot))
            Directory.Delete(_testDataRoot, recursive: true);
    }

    private StorageAssetRepository CreateRepository()
    {
        var storage = new LocalFileStorage();
        var opts = Options.Create(new CandleStorageOptions { DataRoot = _testDataRoot });
        var provider = new FileSystemAvailableAssetsProvider(storage, opts);
        var reader = new FeedManifestReader(storage, NullLogger<FeedManifestReader>.Instance);
        return new StorageAssetRepository(reader, provider, opts, NullLogger<StorageAssetRepository>.Instance);
    }

    private void WriteEquity(string exchange, string symbol)
    {
        var assetDir = Path.Combine(_testDataRoot, exchange, symbol);
        Directory.CreateDirectory(Path.Combine(assetDir, "candles"));
        File.WriteAllText(Path.Combine(assetDir, "candles", "2024-01_5m.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(assetDir, "feeds.json"),
            """{ "feeds": {}, "candles": { "scaleFactor": 100, "intervals": ["5m", "1d"] } }""");
    }

    [Fact]
    public async Task DiscoveredNyseSymbol_ResolvesAsEquityAsset()
    {
        WriteEquity("NYSE", "F");

        var asset = await CreateRepository().GetByNameAsync("F", "NYSE", Ct);

        var equity = Assert.IsType<EquityAsset>(asset);
        Assert.Equal("F", equity.Name);
        Assert.Equal("NYSE", equity.Exchange);
        Assert.Equal(0.01m, equity.TickSize);
        Assert.Equal(SettlementMode.CashAndCarry, equity.Settlement);
    }

    [Fact]
    public async Task DiscoveredNasdaqAndNyseMkt_ResolveAsEquity()
    {
        WriteEquity("NASDAQ", "TSLA");
        WriteEquity("NYSEMKT", "IMO");

        var repo = CreateRepository();

        Assert.IsType<EquityAsset>(await repo.GetByNameAsync("TSLA", "NASDAQ", Ct));
        Assert.IsType<EquityAsset>(await repo.GetByNameAsync("IMO", "NYSEMKT", Ct));
    }

    [Fact]
    public async Task DiscoveredNonEquityExchange_StillResolvesAsCrypto()
    {
        WriteEquity("Binance", "SOLUSDT"); // non-equity exchange → CryptoAsset path unchanged

        var asset = await CreateRepository().GetByNameAsync("SOLUSDT", "Binance", Ct);

        Assert.IsType<CryptoAsset>(asset);
    }

    [Fact]
    public async Task Equity_tick_size_comes_from_feeds_json_scale_factor()
    {
        // TSLA is not a hardcoded seed; scaleFactor 1000 → 3 decimal digits → tick 0.001
        var dir = Path.Combine(_testDataRoot, "NASDAQ", "TSLA");
        Directory.CreateDirectory(Path.Combine(dir, "candles"));
        File.WriteAllText(Path.Combine(dir, "candles", "2024-01_5m.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(dir, "feeds.json"),
            """{ "feeds": {}, "candles": { "scaleFactor": 1000, "intervals": ["5m", "1d"] } }""");

        var asset = await CreateRepository().GetByNameAsync("TSLA", "NASDAQ", Ct);

        var equity = Assert.IsType<EquityAsset>(asset);
        Assert.Equal(0.001m, equity.TickSize);
    }

    [Fact]
    public async Task Equity_whole_unit_scale_factor_yields_unit_tick()
    {
        // scaleFactor 1 → 0 decimal digits → $1 tick, not the $0.01 default.
        var dir = Path.Combine(_testDataRoot, "NYSE", "BRK");
        Directory.CreateDirectory(Path.Combine(dir, "candles"));
        File.WriteAllText(Path.Combine(dir, "candles", "2024-01_5m.csv"), "ts,o,h,l,c,vol\n");
        File.WriteAllText(Path.Combine(dir, "feeds.json"),
            """{ "feeds": {}, "candles": { "scaleFactor": 1, "intervals": ["5m", "1d"] } }""");

        var asset = await CreateRepository().GetByNameAsync("BRK", "NYSE", Ct);

        var equity = Assert.IsType<EquityAsset>(asset);
        Assert.Equal(1m, equity.TickSize);
    }
}
