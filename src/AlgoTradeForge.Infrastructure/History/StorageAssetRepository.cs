using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class StorageAssetRepository(
    IFeedManifestReader manifestReader,
    IAvailableAssetsProvider availableAssetsProvider,
    IOptions<CandleStorageOptions> storageOptions,
    ILogger<StorageAssetRepository> logger) : IAssetRepository
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, Asset>? _assets;

    public async Task<Asset?> GetByNameAsync(string name, string exchange, CancellationToken ct = default)
    {
        var assets = await EnsureLoaded(ct);
        return assets.GetValueOrDefault($"{name}|{exchange}");
    }

    public async Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct = default)
    {
        var assets = await EnsureLoaded(ct);
        return assets.Values.ToList();
    }

    private async Task<Dictionary<string, Asset>> EnsureLoaded(CancellationToken ct)
    {
        if (_assets is not null) return _assets;
        await _gate.WaitAsync(ct);
        try
        {
            return _assets ??= await BuildAssetDictionary(
                manifestReader, availableAssetsProvider, storageOptions.Value.DataRoot, logger, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<Dictionary<string, Asset>> BuildAssetDictionary(
        IFeedManifestReader manifestReader,
        IAvailableAssetsProvider provider,
        string dataRoot,
        ILogger logger,
        CancellationToken ct)
    {
        var dict = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);

        SeedHardcodedAssets(dict);

        var available = await provider.GetAvailableAssets(ct);
        foreach (var info in available)
        {
            var key = info.IsFutures
                ? $"{info.Symbol}_PERP|{info.Exchange}"
                : $"{info.Symbol}|{info.Exchange}";

            // Hardcoded seeds take precedence — they carry richer metadata
            // (order size limits, quantity step sizes) that filesystem discovery can't provide.
            if (dict.ContainsKey(key))
                continue;

            var dirName = info.IsFutures ? $"{info.Symbol}_perp" : info.Symbol;
            var manifest = await manifestReader.Read(dataRoot, info.Exchange, dirName, ct);
            var scaleFactor = manifest?.Candles?.ScaleFactor;
            var decimalDigits = scaleFactor is > 0m ? ScaleFactorToDecimalDigits(scaleFactor.Value) : 2;

            Asset asset = info switch
            {
                { IsFutures: true } => CryptoPerpetualAsset.Create(info.Symbol, info.Exchange, decimalDigits, margin: 0.05m),
                // US cash-equity exchanges (e.g. the imported Stooq archive) settle cash-and-carry,
                // not as crypto spot. Filesystem discovery can't carry per-symbol tick/lot metadata,
                // so tick size comes from feeds.json scaleFactor (0.01 default when the manifest is absent).
                _ when UsEquityExchanges.Contains(info.Exchange) =>
                    new EquityAsset
                    {
                        Name = info.Symbol,
                        Exchange = info.Exchange,
                        TickSize = EquityTickSize(scaleFactor),
                    },
                _ => CryptoAsset.Create(info.Symbol, info.Exchange, decimalDigits),
            };

            dict[key] = asset;
        }

        logger.LogInformation("StorageAssetRepository loaded {Count} assets", dict.Count);
        return dict;
    }

    private static void SeedHardcodedAssets(Dictionary<string, Asset> dict)
    {
        dict["BTCUSDT|Binance"] = CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);
        dict["ETHUSDT|Binance"] = CryptoAsset.Create("ETHUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.0001m, maxOrderQuantity: 9000m, quantityStepSize: 0.0001m);

        dict["BTCUSDT_PERP|Binance"] = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", decimalDigits: 2,
            margin: 0.05m,
            minOrderQuantity: 0.001m, maxOrderQuantity: 500m, quantityStepSize: 0.001m);
        dict["ETHUSDT_PERP|Binance"] = CryptoPerpetualAsset.Create("ETHUSDT", "Binance", decimalDigits: 2,
            margin: 0.05m,
            minOrderQuantity: 0.001m, maxOrderQuantity: 10000m, quantityStepSize: 0.001m);

        dict["AAPL|NASDAQ"] = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        dict["MSFT|NASDAQ"] = new EquityAsset { Name = "MSFT", Exchange = "NASDAQ" };
        dict["ES|CME"] = new FutureAsset { Name = "ES", Exchange = "CME", Multiplier = 50m, TickSize = 0.25m, MarginRequirement = 15000m };
        dict["MES|CME"] = new FutureAsset { Name = "MES", Exchange = "CME", Multiplier = 5m, TickSize = 0.25m, MarginRequirement = 1500m };
    }

    // Whole-unit archives (scaleFactor 1 → 0 decimal digits) get a $1 tick, not $0.01.
    // A missing manifest (scaleFactor null) keeps the $0.01 equity default.
    private static decimal EquityTickSize(decimal? scaleFactor) =>
        scaleFactor is > 0m
            ? 1m / (decimal)Math.Pow(10, ScaleFactorToDecimalDigits(scaleFactor.Value))
            : 0.01m;

    private static int ScaleFactorToDecimalDigits(decimal scaleFactor)
        => Math.Clamp((int)Math.Round(Math.Log10((double)scaleFactor)), 0, 10);
}
