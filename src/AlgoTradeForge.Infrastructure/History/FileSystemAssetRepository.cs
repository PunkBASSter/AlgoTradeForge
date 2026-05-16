using System.Text.Json;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.IO;
using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class FileSystemAssetRepository(
    IFileStorage storage,
    IAvailableAssetsProvider availableAssetsProvider,
    IOptions<CandleStorageOptions> storageOptions,
    ILogger<FileSystemAssetRepository> logger) : IAssetRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
                storage, availableAssetsProvider, storageOptions.Value.DataRoot, logger, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<Dictionary<string, Asset>> BuildAssetDictionary(
        IFileStorage storage,
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

            var decimalDigits = await ReadDecimalDigitsFromFeedsJson(storage, dataRoot, info, logger, ct);

            Asset asset = info.IsFutures
                ? CryptoPerpetualAsset.Create(info.Symbol, info.Exchange, decimalDigits, margin: 0.05m)
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

    private static async Task<int> ReadDecimalDigitsFromFeedsJson(
        IFileStorage storage, string dataRoot, AvailableAssetInfo info, ILogger logger, CancellationToken ct)
    {
        var dirName = info.IsFutures ? $"{info.Symbol}_perp" : info.Symbol;
        var feedsJsonPath = Path.Combine(dataRoot, info.Exchange, dirName, "feeds.json");

        if (!await storage.Exists(feedsJsonPath, ct))
            return 2;

        try
        {
            await using var stream = await storage.OpenRead(feedsJsonPath, ct);
            var metadata = await JsonSerializer.DeserializeAsync<FeedMetadata>(stream, JsonOptions, ct);

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
