using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class FileSystemAssetRepository(
    IAvailableAssetsProvider availableAssetsProvider,
    IOptions<CandleStorageOptions> storageOptions,
    ILogger<FileSystemAssetRepository> logger) : IAssetRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Lazy<Dictionary<string, Asset>> _assets = new(() =>
        BuildAssetDictionary(availableAssetsProvider, storageOptions.Value.DataRoot, logger));

    public Task<Asset?> GetByNameAsync(string name, string exchange, CancellationToken ct = default)
        => Task.FromResult(_assets.Value.GetValueOrDefault($"{name}|{exchange}"));

    public Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Asset>>(_assets.Value.Values.ToList());

    private static Dictionary<string, Asset> BuildAssetDictionary(
        IAvailableAssetsProvider provider,
        string dataRoot,
        ILogger logger)
    {
        var dict = new Dictionary<string, Asset>(StringComparer.OrdinalIgnoreCase);

        // 1. Seed hardcoded overrides for known assets
        SeedHardcodedAssets(dict);

        // 2. Discover assets from filesystem
        foreach (var info in provider.GetAvailableAssets())
        {
            var key = info.IsFutures
                ? $"{info.Symbol}_PERP|{info.Exchange}"
                : $"{info.Symbol}|{info.Exchange}";

            // Hardcoded seeds take precedence — they carry richer metadata
            // (order size limits, quantity step sizes) that filesystem discovery can't provide.
            if (dict.ContainsKey(key))
                continue;

            var decimalDigits = ReadDecimalDigitsFromFeedsJson(dataRoot, info, logger);

            Asset asset = info.IsFutures
                ? CryptoPerpetualAsset.Create(info.Symbol, info.Exchange, decimalDigits)
                : CryptoAsset.Create(info.Symbol, info.Exchange, decimalDigits);

            dict[key] = asset;
        }

        logger.LogInformation("FileSystemAssetRepository loaded {Count} assets", dict.Count);
        return dict;
    }

    private static void SeedHardcodedAssets(Dictionary<string, Asset> dict)
    {
        dict["BTCUSDT|Binance"] = CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);
        dict["ETHUSDT|Binance"] = CryptoAsset.Create("ETHUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.0001m, maxOrderQuantity: 9000m, quantityStepSize: 0.0001m);

        // Perpetual variants for hardcoded crypto assets
        dict["BTCUSDT_PERP|Binance"] = CryptoPerpetualAsset.Create("BTCUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.001m, maxOrderQuantity: 500m, quantityStepSize: 0.001m);
        dict["ETHUSDT_PERP|Binance"] = CryptoPerpetualAsset.Create("ETHUSDT", "Binance", decimalDigits: 2,
            minOrderQuantity: 0.001m, maxOrderQuantity: 10000m, quantityStepSize: 0.001m);

        dict["AAPL|NASDAQ"] = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        dict["MSFT|NASDAQ"] = new EquityAsset { Name = "MSFT", Exchange = "NASDAQ" };
        dict["ES|CME"] = new FutureAsset { Name = "ES", Exchange = "CME", Multiplier = 50m, TickSize = 0.25m, MarginRequirement = 15000m };
        dict["MES|CME"] = new FutureAsset { Name = "MES", Exchange = "CME", Multiplier = 5m, TickSize = 0.25m, MarginRequirement = 1500m };
    }

    private static int ReadDecimalDigitsFromFeedsJson(string dataRoot, AvailableAssetInfo info, ILogger logger)
    {
        var dirName = info.IsFutures ? $"{info.Symbol}_fut" : info.Symbol;
        var feedsJsonPath = Path.Combine(dataRoot, info.Exchange, dirName, "feeds.json");

        if (!File.Exists(feedsJsonPath))
            return 2;

        try
        {
            using var fs = new FileStream(feedsJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var metadata = JsonSerializer.Deserialize<FeedMetadata>(fs, JsonOptions);

            if (metadata?.Candles?.ScaleFactor is > 0)
                return ScaleFactorToDecimalDigits(metadata.Candles.ScaleFactor);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Failed to read feeds.json at {Path}, defaulting to 2 decimal digits", feedsJsonPath);
        }

        return 2;
    }

    private static int ScaleFactorToDecimalDigits(decimal scaleFactor)
        => Math.Clamp((int)Math.Round(Math.Log10((double)scaleFactor)), 0, 10);
}
