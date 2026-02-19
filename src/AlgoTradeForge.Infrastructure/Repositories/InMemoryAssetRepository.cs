using AlgoTradeForge.Application.Repositories;
using AlgoTradeForge.Domain;

namespace AlgoTradeForge.Infrastructure.Repositories;

public sealed class InMemoryAssetRepository : IAssetRepository
{
    private static readonly Dictionary<string, Asset> Assets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTCUSDT|Binance"] = Asset.Crypto("BTCUSDT", "Binance", decimalDigits: 2, historyStart: new DateOnly(2024, 1, 1)),
        ["ETHUSDT|Binance"] = Asset.Crypto("ETHUSDT", "Binance", decimalDigits: 2, historyStart: new DateOnly(2024, 1, 1)),
        ["AAPL|NASDAQ"] = Asset.Equity("AAPL", "NASDAQ"),
        ["MSFT|NASDAQ"] = Asset.Equity("MSFT", "NASDAQ"),
        ["ES|CME"] = Asset.Future("ES", "CME", multiplier: 50m, tickSize: 0.25m, margin: 15000m),
        ["MES|CME"] = Asset.Future("MES", "CME", multiplier: 5m, tickSize: 0.25m, margin: 1500m),
    };

    public Task<Asset?> GetByNameAsync(string name, string exchange, CancellationToken ct = default)
        => Task.FromResult(Assets.GetValueOrDefault($"{name}|{exchange}"));

    public Task<IReadOnlyList<Asset>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Asset>>(Assets.Values.ToList());
}
