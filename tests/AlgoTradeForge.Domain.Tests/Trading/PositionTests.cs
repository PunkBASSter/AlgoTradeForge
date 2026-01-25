using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Trading;

public class PositionTests
{
    [Fact]
    public void Constructor_WithDefaults_InitializesToZero()
    {
        var position = new Position(TestAssets.Aapl);

        Assert.Equal(TestAssets.Aapl, position.Asset);
        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0m, position.AverageEntryPrice);
        Assert.Equal(0m, position.RealizedPnl);
    }

    [Fact]
    public void Constructor_WithInitialValues_SetsCorrectly()
    {
        var position = new Position(TestAssets.Aapl, quantity: 100m, averageEntryPrice: 150m, realizedPnl: 500m);

        Assert.Equal(100m, position.Quantity);
        Assert.Equal(150m, position.AverageEntryPrice);
        Assert.Equal(500m, position.RealizedPnl);
    }

    [Fact]
    public void UnrealizedPnl_FlatPosition_ReturnsZero()
    {
        var position = new Position(TestAssets.Aapl);

        Assert.Equal(0m, position.UnrealizedPnl(100m));
    }

    [Theory]
    [InlineData(100, 150, 160, 1000)]  // Long: (160-150)*100*1 = 1000
    [InlineData(100, 150, 140, -1000)] // Long: (140-150)*100*1 = -1000
    [InlineData(-50, 200, 180, 1000)]  // Short: (180-200)*-50*1 = 1000
    [InlineData(-50, 200, 220, -1000)] // Short: (220-200)*-50*1 = -1000
    public void UnrealizedPnl_EquityPosition_CalculatesCorrectly(
        decimal quantity, decimal entryPrice, decimal currentPrice, decimal expectedPnl)
    {
        var position = new Position(TestAssets.Aapl, quantity, entryPrice);

        Assert.Equal(expectedPnl, position.UnrealizedPnl(currentPrice));
    }

    [Theory]
    [InlineData(1, 5000, 5010, 500)]   // Long ES: (5010-5000)*1*50 = 500
    [InlineData(-2, 5000, 5020, -2000)] // Short ES: (5020-5000)*-2*50 = -2000
    public void UnrealizedPnl_FuturesPosition_AppliesMultiplier(
        decimal quantity, decimal entryPrice, decimal currentPrice, decimal expectedPnl)
    {
        var position = new Position(TestAssets.EsMini, quantity, entryPrice);

        Assert.Equal(expectedPnl, position.UnrealizedPnl(currentPrice));
    }

    [Fact]
    public void Apply_BuyOnFlatPosition_OpensLong()
    {
        var position = new Position(TestAssets.Aapl);
        var fill = TestFills.BuyAapl(150m, 100m);

        position.Apply(fill);

        Assert.Equal(100m, position.Quantity);
        Assert.Equal(150m, position.AverageEntryPrice);
        Assert.Equal(0m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_SellOnFlatPosition_OpensShort()
    {
        var position = new Position(TestAssets.Aapl);
        var fill = TestFills.SellAapl(150m, 100m);

        position.Apply(fill);

        Assert.Equal(-100m, position.Quantity);
        Assert.Equal(150m, position.AverageEntryPrice);
        Assert.Equal(0m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_AddToLong_CalculatesWeightedAveragePrice()
    {
        // Start with 100 shares at $150
        var position = new Position(TestAssets.Aapl, 100m, 150m);
        // Add 100 shares at $160
        var fill = TestFills.BuyAapl(160m, 100m);

        position.Apply(fill);

        Assert.Equal(200m, position.Quantity);
        // Weighted average: (100*150 + 100*160) / 200 = 155
        Assert.Equal(155m, position.AverageEntryPrice);
        Assert.Equal(0m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_AddToShort_CalculatesWeightedAveragePrice()
    {
        // Start with -100 shares at $150
        var position = new Position(TestAssets.Aapl, -100m, 150m);
        // Add -50 shares at $140
        var fill = TestFills.SellAapl(140m, 50m);

        position.Apply(fill);

        Assert.Equal(-150m, position.Quantity);
        // Weighted average: (-100*150 + -50*140) / -150 = 146.67
        Assert.True(Math.Abs(146.67m - position.AverageEntryPrice) < 0.01m);
        Assert.Equal(0m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_PartialCloseWithProfit_RealizesProfit()
    {
        // Long 100 at $150
        var position = new Position(TestAssets.Aapl, 100m, 150m);
        // Sell 40 at $170 (profit: 40 * (170-150) = 800)
        var fill = TestFills.SellAapl(170m, 40m);

        position.Apply(fill);

        Assert.Equal(60m, position.Quantity);
        Assert.Equal(150m, position.AverageEntryPrice);
        Assert.Equal(800m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_PartialCloseWithLoss_RealizesLoss()
    {
        // Long 100 at $150
        var position = new Position(TestAssets.Aapl, 100m, 150m);
        // Sell 40 at $130 (loss: 40 * (130-150) = -800)
        var fill = TestFills.SellAapl(130m, 40m);

        position.Apply(fill);

        Assert.Equal(60m, position.Quantity);
        Assert.Equal(150m, position.AverageEntryPrice);
        Assert.Equal(-800m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_FullClose_RealizesAllPnl()
    {
        // Long 100 at $150
        var position = new Position(TestAssets.Aapl, 100m, 150m);
        // Sell 100 at $160 (profit: 100 * (160-150) = 1000)
        var fill = TestFills.SellAapl(160m, 100m);

        position.Apply(fill);

        Assert.Equal(0m, position.Quantity);
        Assert.Equal(1000m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_LongToShortReversal_RealizesAndOpensNew()
    {
        // Long 100 at $150
        var position = new Position(TestAssets.Aapl, 100m, 150m);
        // Sell 150 at $160 (close 100 with profit 1000, open short 50)
        var fill = TestFills.SellAapl(160m, 150m);

        position.Apply(fill);

        Assert.Equal(-50m, position.Quantity);
        Assert.Equal(160m, position.AverageEntryPrice);
        Assert.Equal(1000m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_ShortToLongReversal_RealizesAndOpensNew()
    {
        // Short 100 at $150
        var position = new Position(TestAssets.Aapl, -100m, 150m);
        // Buy 150 at $140 (close 100 with profit 1000, open long 50)
        var fill = TestFills.BuyAapl(140m, 150m);

        position.Apply(fill);

        Assert.Equal(50m, position.Quantity);
        Assert.Equal(140m, position.AverageEntryPrice);
        Assert.Equal(1000m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_FuturesPartialClose_AppliesMultiplier()
    {
        // Long 2 ES at 5000
        var position = new Position(TestAssets.EsMini, 2m, 5000m);
        // Sell 1 at 5020 (profit: 1 * (5020-5000) * 50 = 1000)
        var fill = TestFills.SellEs(5020m, 1m);

        position.Apply(fill);

        Assert.Equal(1m, position.Quantity);
        Assert.Equal(1000m, position.RealizedPnl);
    }

    [Fact]
    public void Apply_FuturesReversal_AppliesMultiplier()
    {
        // Long 2 ES at 5000
        var position = new Position(TestAssets.EsMini, 2m, 5000m);
        // Sell 4 at 5010 (close 2 with profit: 2*(5010-5000)*50 = 1000, open short 2)
        var fill = TestFills.SellEs(5010m, 4m);

        position.Apply(fill);

        Assert.Equal(-2m, position.Quantity);
        Assert.Equal(5010m, position.AverageEntryPrice);
        Assert.Equal(1000m, position.RealizedPnl);
    }

    [Fact]
    public void Reset_ClearsAllValues()
    {
        var position = new Position(TestAssets.Aapl, 100m, 150m, 500m);

        position.Reset();

        Assert.Equal(0m, position.Quantity);
        Assert.Equal(0m, position.AverageEntryPrice);
        Assert.Equal(0m, position.RealizedPnl);
    }
}
