using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Reporting;

public class MetricsCalculatorTickTests
{
    private readonly MetricsCalculator _sut = new();

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TickMetrics_QuantityIndependent_PerUnitPriceMove()
    {
        var exitTime = new DateTimeOffset(2024, 3, 15, 14, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Buy(TestAssets.Aapl, 100L, 10m, timestamp: Start),
            TestFills.Sell(TestAssets.Aapl, 121L, 10m, timestamp: exitTime),
        };
        var equityCurve = new List<long> { 10_000L, 10_210L };

        var (metrics, trades) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        // Per-unit move is 21 ticks regardless of the 10-unit quantity
        Assert.Equal(21L, trades[0].PriceMoveTicks);
        Assert.Equal(21L, metrics.NetTicks);
        Assert.Equal(21.0, metrics.AvgTicksPerTrade);
    }

    [Fact]
    public void TickMetrics_ShortTrade_PositiveTicksOnPriceDrop()
    {
        var exitTime = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Sell(TestAssets.Aapl, 120L, 5m, timestamp: Start),
            TestFills.Buy(TestAssets.Aapl, 110L, 5m, timestamp: exitTime),
        };
        var equityCurve = new List<long> { 10_000L, 10_050L };

        var (metrics, trades) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        Assert.Equal(10L, trades[0].PriceMoveTicks);
        Assert.Equal(10L, metrics.NetTicks);
    }

    [Fact]
    public void TickMetrics_MixedTrades_NetAndProfitFactor()
    {
        var exit1 = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var entry2 = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var exit2 = new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Buy(TestAssets.Aapl, 100L, 10m, timestamp: Start),
            TestFills.Sell(TestAssets.Aapl, 120L, 10m, timestamp: exit1),  // +20 ticks
            TestFills.Buy(TestAssets.Aapl, 130L, 5m, timestamp: entry2),
            TestFills.Sell(TestAssets.Aapl, 125L, 5m, timestamp: exit2),   // -5 ticks
        };
        var equityCurve = new List<long> { 10_000L, 10_200L, 10_200L, 10_175L };

        var (metrics, _) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        Assert.Equal(15L, metrics.NetTicks);
        Assert.Equal(7.5, metrics.AvgTicksPerTrade);
        Assert.Equal(4.0, metrics.TickProfitFactor); // 20 / 5
    }
}
