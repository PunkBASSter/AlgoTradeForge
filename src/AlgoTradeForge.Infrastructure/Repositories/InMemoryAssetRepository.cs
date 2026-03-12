using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;

namespace AlgoTradeForge.Infrastructure.Repositories;

public sealed class InMemoryAssetRepository : IAssetRepository
{
    private static readonly Dictionary<string, Asset> Assets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTCUSDT|Binance"] = CryptoAsset.Create("BTCUSDT", "Binance", decimalDigits: 2, historyStart: new DateOnly(2024, 1, 1),
            minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m),
        ["ETHUSDT|Binance"] = CryptoAsset.Create("ETHUSDT", "Binance", decimalDigits: 2, historyStart: new DateOnly(2024, 1, 1),
            minOrderQuantity: 0.0001m, maxOrderQuantity: 9000m, quantityStepSize: 0.0001m),
        ["AAPL|NASDAQ"] = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" },
        ["MSFT|NASDAQ"] = new EquityAsset { Name = "MSFT", Exchange = "NASDAQ" },
        ["ES|CME"] = new FutureAsset { Name = "ES", Exchange = "CME", Multiplier = 50m, TickSize = 0.25m, MarginRequirement = 15000m },
        ["MES|CME"] = new FutureAsset { Name = "MES", Exchange = "CME", Multiplier = 5m, TickSize = 0.25m, MarginRequirement = 1500m },
    };

    public Task<Asset?> GetByNameAsync(string name, string exchange, CancellationToken ct = default)
        => Task.FromResult(Assets.GetValueOrDefault($"{name}|{exchange}"));

    public Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Asset>>(Assets.Values.ToList());
}
