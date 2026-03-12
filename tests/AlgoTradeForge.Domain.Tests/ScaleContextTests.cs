using AlgoTradeForge.Domain.Tests.TestUtilities;
using Xunit;

namespace AlgoTradeForge.Domain.Tests;

public class ScaleContextTests
{
    [Fact]
    public void Constructor_FromAsset_SetsTickSizeAndScaleFactor()
    {
        var scale = new ScaleContext(TestAssets.Aapl);

        Assert.Equal(0.01m, scale.TickSize);
        Assert.Equal(100m, scale.ScaleFactor);
    }

    [Fact]
    public void Constructor_FromTickSize_SetsTickSizeAndScaleFactor()
    {
        var scale = new ScaleContext(0.00000001m); // BTC 8 decimals

        Assert.Equal(0.00000001m, scale.TickSize);
        Assert.Equal(100_000_000m, scale.ScaleFactor);
    }

    [Theory]
    [InlineData(10000.0, 0.01, 1000000L)]      // $10,000 with tick 0.01
    [InlineData(0.10, 0.01, 10L)]                // $0.10 commission
    [InlineData(100.005, 0.01, 10001L)]           // Half-cent rounds up
    public void AmountToTicks_ConvertsDecimalToTicks(decimal value, decimal tickSize, long expected)
    {
        var scale = new ScaleContext(tickSize);
        Assert.Equal(expected, scale.AmountToTicks(value));
    }

    [Theory]
    [InlineData(1000000L, 0.01, 10000.00)]   // 1M ticks → $10,000
    [InlineData(10L, 0.01, 0.10)]             // 10 ticks → $0.10
    public void TicksToAmount_ConvertsTicks(long ticks, decimal tickSize, decimal expected)
    {
        var scale = new ScaleContext(tickSize);
        Assert.Equal(expected, scale.TicksToAmount(ticks));
    }

    [Theory]
    [InlineData(1000000.0, 0.01, 10000.00)]   // 1M ticks → $10,000
    [InlineData(10.0, 0.01, 0.10)]             // 10 ticks → $0.10
    [InlineData(500.5, 0.01, 5.005)]           // Fractional ticks preserved
    public void TicksToAmount_DecimalOverload_ConvertsTicks(decimal ticks, decimal tickSize, decimal expected)
    {
        var scale = new ScaleContext(tickSize);
        Assert.Equal(expected, scale.TicksToAmount(ticks));
    }

    [Theory]
    [InlineData(95234.56, 0.01, 9523456L)]    // Market price to ticks
    [InlineData(0.00095234, 0.00000001, 95234L)] // BTC satoshi precision
    public void FromMarketPrice_ParsesCorrectly(decimal price, decimal tickSize, long expected)
    {
        var scale = new ScaleContext(tickSize);
        Assert.Equal(expected, scale.FromMarketPrice(price));
    }

    [Theory]
    [InlineData(9523456L, 0.01, 95234.56)]
    [InlineData(95234L, 0.00000001, 0.00095234)]
    public void ToMarketPrice_ConvertsBack(long ticks, decimal tickSize, decimal expected)
    {
        var scale = new ScaleContext(tickSize);
        Assert.Equal(expected, scale.ToMarketPrice(ticks));
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(0.25)]
    [InlineData(0.00000001)]
    public void RoundTrip_AmountToTicksThenBack_PreservesValue(decimal tickSize)
    {
        var scale = new ScaleContext(tickSize);
        var original = 12345.67m;
        var ticks = scale.AmountToTicks(original);
        var restored = scale.TicksToAmount(ticks);

        // Restored value should be within one tick of original
        Assert.True(Math.Abs(restored - original) <= tickSize,
            $"Round-trip drift too large: {original} → {ticks} → {restored}");
    }

    [Fact]
    public void FromMarketPrice_HalfTick_RoundsAwayFromZero()
    {
        // Price 100.005 with tick 0.01 → 100.005 / 0.01 = 10000.5 → rounds to 10001
        var scale = new ScaleContext(0.01m);
        Assert.Equal(10001L, scale.FromMarketPrice(100.005m));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    [InlineData(-1)]
    public void Constructor_FromTickSize_ThrowsOnZeroOrNegative(decimal tickSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleContext(tickSize));
    }

    [Fact]
    public void Constructor_FromAsset_ThrowsOnZeroTickSize()
    {
        var asset = new EquityAsset { Name = "BAD", Exchange = "TEST", TickSize = 0m };
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScaleContext(asset));
    }
}
