using AlgoTradeForge.Infrastructure.Repositories;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Repositories;

public class InMemoryAssetRepositoryTests
{
    private readonly InMemoryAssetRepository _repo = new();

    [Fact]
    public async Task GetByNameAsync_CaseInsensitive_ReturnsAsset()
    {
        var asset = await _repo.GetByNameAsync("btcusdt", "binance", TestContext.Current.CancellationToken);

        Assert.NotNull(asset);
        Assert.Equal("BTCUSDT", asset.Name);
        Assert.Equal("Binance", asset.Exchange);
    }

    [Fact]
    public async Task GetByNameAsync_SameSymbolDifferentExchange_ReturnsDifferentAssets()
    {
        var binance = await _repo.GetByNameAsync("BTCUSDT", "Binance", TestContext.Current.CancellationToken);
        var unknown = await _repo.GetByNameAsync("BTCUSDT", "Bybit", TestContext.Current.CancellationToken);

        Assert.NotNull(binance);
        Assert.Equal("Binance", binance.Exchange);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task GetByNameAsync_UnknownAsset_ReturnsNull()
    {
        var result = await _repo.GetByNameAsync("DOESNOTEXIST", "Binance", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllAssets()
    {
        var all = await _repo.GetAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(6, all.Count);
    }
}
