using Xunit;

namespace AlgoTradeForge.Domain.Tests;

public class AssetTests
{
    [Theory]
    [InlineData(1.5, 0.5, 1.5)]   // exact multiple → unchanged
    [InlineData(1.7, 0.5, 1.5)]   // rounds down to nearest step
    [InlineData(0.3, 0.5, 0.0)]   // below one step → floor to 0
    [InlineData(10.0, 1.0, 10.0)] // integer step
    [InlineData(0.00037, 0.00001, 0.00037)] // crypto precision
    [InlineData(0.00039, 0.0001, 0.0003)]   // rounds down
    public void RoundQuantityDown_ReturnsFlooredMultipleOfStep(decimal quantity, decimal step, decimal expected)
    {
        var asset = Asset.Crypto("TEST", "TEST", 2, quantityStepSize: step);
        Assert.Equal(expected, asset.RoundQuantityDown(quantity));
    }

    [Fact]
    public void RoundQuantityDown_ZeroStep_ReturnsOriginal()
    {
        var asset = Asset.Crypto("TEST", "TEST", 2, quantityStepSize: 0m);
        Assert.Equal(1.23456m, asset.RoundQuantityDown(1.23456m));
    }

    [Fact]
    public void RoundQuantityDown_NegativeStep_ReturnsOriginal()
    {
        var asset = Asset.Crypto("TEST", "TEST", 2, quantityStepSize: -1m);
        Assert.Equal(1.5m, asset.RoundQuantityDown(1.5m));
    }

    [Fact]
    public void EquityFactory_DefaultConstraints()
    {
        var asset = Asset.Equity("AAPL", "NASDAQ");
        Assert.Equal(1m, asset.MinOrderQuantity);
        Assert.Equal(decimal.MaxValue, asset.MaxOrderQuantity);
        Assert.Equal(1m, asset.QuantityStepSize);
    }

    [Fact]
    public void FutureFactory_DefaultConstraints()
    {
        var asset = Asset.Future("ES", "CME", 50m, 0.25m);
        Assert.Equal(1m, asset.MinOrderQuantity);
        Assert.Equal(decimal.MaxValue, asset.MaxOrderQuantity);
        Assert.Equal(1m, asset.QuantityStepSize);
    }

    [Fact]
    public void CryptoFactory_DefaultConstraints()
    {
        var asset = Asset.Crypto("TEST", "Binance", 2);
        Assert.Equal(0m, asset.MinOrderQuantity);
        Assert.Equal(decimal.MaxValue, asset.MaxOrderQuantity);
        Assert.Equal(0m, asset.QuantityStepSize);
    }

    [Fact]
    public void CryptoFactory_CustomConstraints()
    {
        var asset = Asset.Crypto("BTCUSDT", "Binance", 2,
            minOrderQuantity: 0.00001m, maxOrderQuantity: 9000m, quantityStepSize: 0.00001m);
        Assert.Equal(0.00001m, asset.MinOrderQuantity);
        Assert.Equal(9000m, asset.MaxOrderQuantity);
        Assert.Equal(0.00001m, asset.QuantityStepSize);
    }
}
