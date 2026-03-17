using AlgoTradeForge.Domain;
using AlgoTradeForge.Infrastructure.History;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.History;

public class AssetDirectoryNameTests
{
    [Fact]
    public void From_CryptoAsset_ReturnsName()
    {
        var asset = new CryptoAsset { Name = "BTCUSDT", Exchange = "binance", Multiplier = 100m };

        var result = AssetDirectoryName.From(asset);

        Assert.Equal("BTCUSDT", result);
    }

    [Fact]
    public void From_CryptoPerpetualAsset_ReturnsNameWithFutSuffix()
    {
        var asset = new CryptoPerpetualAsset { Name = "BTCUSDT", Exchange = "binance", Multiplier = 100m };

        var result = AssetDirectoryName.From(asset);

        Assert.Equal("BTCUSDT_fut", result);
    }

    [Fact]
    public void From_EquityAsset_ReturnsName()
    {
        var asset = new EquityAsset { Name = "AAPL", Exchange = "nasdaq", Multiplier = 100m };

        var result = AssetDirectoryName.From(asset);

        Assert.Equal("AAPL", result);
    }

    [Fact]
    public void From_FutureAsset_ReturnsNameWithFutSuffix()
    {
        var asset = FutureAsset.Create("ESZ4", "cme", 100m, tickSize: 0.25m, minOrderQuantity: 1m, quantityStepSize: 1m);

        var result = AssetDirectoryName.From(asset);

        Assert.Equal("ESZ4_fut", result);
    }
}
