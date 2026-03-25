using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Reporting;

public class MetricsCalculatorTradeTests
{
    private readonly MetricsCalculator _sut = new();

    private static readonly DateTimeOffset Start = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2024, 12, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SingleRoundTrip_ProducesOneClosedTrade()
    {
        var exitTime = new DateTimeOffset(2024, 3, 15, 14, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Buy(TestAssets.Aapl, 100L, 10m, timestamp: Start),
            TestFills.Sell(TestAssets.Aapl, 121L, 10m, timestamp: exitTime),
        };
        var equityCurve = new List<long> { 10_000L, 10_210L };

        var (_, trades) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        Assert.Single(trades);
        Assert.Equal(exitTime.ToUnixTimeMilliseconds(), trades[0].ExitTimestampMs);
        // PnL = (121-100) * 10 * 1 (multiplier) = 210
        Assert.Equal(210L, trades[0].RealizedPnl);
    }

    [Fact]
    public void PartialClose_ProducesOneClosedTrade()
    {
        var exitTime = new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Buy(TestAssets.Aapl, 100L, 10m, timestamp: Start),
            TestFills.Sell(TestAssets.Aapl, 110L, 5m, timestamp: exitTime),
        };
        var equityCurve = new List<long> { 10_000L, 10_050L };

        var (_, trades) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        Assert.Single(trades);
        // PnL = (110-100) * 5 * 1 = 50
        Assert.Equal(50L, trades[0].RealizedPnl);
        Assert.Equal(exitTime.ToUnixTimeMilliseconds(), trades[0].ExitTimestampMs);
    }

    [Fact]
    public void MultipleRoundTrips_ProducesMultipleClosedTrades()
    {
        var exit1 = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var entry2 = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var exit2 = new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Buy(TestAssets.Aapl, 100L, 10m, timestamp: Start),
            TestFills.Sell(TestAssets.Aapl, 120L, 10m, timestamp: exit1),
            TestFills.Buy(TestAssets.Aapl, 130L, 5m, timestamp: entry2),
            TestFills.Sell(TestAssets.Aapl, 125L, 5m, timestamp: exit2),
        };
        var equityCurve = new List<long> { 10_000L, 10_200L, 10_200L, 10_175L };

        var (_, trades) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        Assert.Equal(2, trades.Count);
        Assert.Equal(200L, trades[0].RealizedPnl); // win: (120-100)*10
        Assert.Equal(-25L, trades[1].RealizedPnl); // loss: (125-130)*5
    }

    [Fact]
    public void ZeroFills_ProducesEmptyTradeList()
    {
        var fills = new List<Fill>();
        var equityCurve = new List<long> { 10_000L };

        var (_, trades) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        Assert.Empty(trades);
    }

    [Fact]
    public void LosingTrade_RecordsNegativePnl()
    {
        var exitTime = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Buy(TestAssets.Aapl, 100L, 10m, timestamp: Start),
            TestFills.Sell(TestAssets.Aapl, 90L, 10m, timestamp: exitTime),
        };
        var equityCurve = new List<long> { 10_000L, 9_900L };

        var (_, trades) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        Assert.Single(trades);
        Assert.Equal(-100L, trades[0].RealizedPnl);
    }

    [Fact]
    public void RoundTrip_WithCommission_ReturnsNetPnl()
    {
        var exitTime = new DateTimeOffset(2024, 3, 15, 14, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Buy(TestAssets.Aapl, 100L, 10m, commission: 5L, timestamp: Start),
            TestFills.Sell(TestAssets.Aapl, 121L, 10m, commission: 5L, timestamp: exitTime),
        };
        var equityCurve = new List<long> { 10_000L, 10_200L };

        var (_, trades) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        Assert.Single(trades);
        // Gross PnL = (121-100) * 10 * 1 = 210, round-trip commission = 5+5 = 10
        // Net PnL = 210 - 10 = 200
        Assert.Equal(200L, trades[0].RealizedPnl);
    }

    [Fact]
    public void PartialClose_WithCommission_AttributesProportionally()
    {
        var exitTime = new DateTimeOffset(2024, 6, 1, 10, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Buy(TestAssets.Aapl, 100L, 10m, commission: 10L, timestamp: Start),
            TestFills.Sell(TestAssets.Aapl, 110L, 5m, commission: 5L, timestamp: exitTime),
        };
        var equityCurve = new List<long> { 10_000L, 10_035L };

        var (_, trades) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        Assert.Single(trades);
        // Gross PnL = (110-100) * 5 * 1 = 50
        // Entry commission attributed = 10 * (5/10) = 5, exit commission = 5
        // Net PnL = 50 - 5 - 5 = 40
        Assert.Equal(40L, trades[0].RealizedPnl);
    }

    [Fact]
    public void GrossMetrics_UnchangedByCommissionFix()
    {
        var exitTime = new DateTimeOffset(2024, 3, 15, 14, 0, 0, TimeSpan.Zero);
        var fills = new List<Fill>
        {
            TestFills.Buy(TestAssets.Aapl, 100L, 10m, commission: 5L, timestamp: Start),
            TestFills.Sell(TestAssets.Aapl, 121L, 10m, commission: 5L, timestamp: exitTime),
        };
        var equityCurve = new List<long> { 10_000L, 10_200L };

        var (metrics, _) = _sut.Calculate(fills, equityCurve, 10_000L, Start, End);

        // GrossProfit uses gross PnL (210), not net (200)
        Assert.Equal(210m, metrics.GrossProfit);
        Assert.Equal(0m, metrics.GrossLoss);
        Assert.Equal(10m, metrics.TotalCommissions);
        // NetProfit = GrossProfit - GrossLoss - TotalCommissions = 210 - 0 - 10 = 200
        Assert.Equal(200m, metrics.NetProfit);
    }
}
