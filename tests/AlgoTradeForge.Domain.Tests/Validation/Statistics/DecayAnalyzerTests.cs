using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class DecayAnalyzerTests
{
    [Fact]
    public void DecliningPnl_NegativeSlope()
    {
        // Strong early returns, declining over time
        var pnl = new double[200];
        for (var i = 0; i < 200; i++)
            pnl[i] = 20.0 - i * 0.15; // Starts at 20, ends at ~-10

        var result = DecayAnalyzer.Analyze(pnl, 10000.0, rollingWindow: 30);

        Assert.True(result.IsDecaying, "Expected IsDecaying=true for declining P&L");
        Assert.True(result.SlopeCoefficient < 0,
            $"Expected negative slope, got {result.SlopeCoefficient:F6}");
    }

    [Fact]
    public void ConstantPercentageReturn_NearZeroSlope()
    {
        // Constant *percentage* returns → rolling Sharpe should be stable
        // P&L must grow proportionally with equity: pnl[i] = rate * equity[i]
        var pnl = new double[200];
        var equity = 10000.0;
        const double rate = 0.001; // 0.1% per bar
        for (var i = 0; i < 200; i++)
        {
            pnl[i] = rate * equity;
            equity += pnl[i];
        }

        var result = DecayAnalyzer.Analyze(pnl, 10000.0, rollingWindow: 30);

        // Constant percentage returns → rolling Sharpe is constant → slope ≈ 0
        Assert.True(Math.Abs(result.SlopeCoefficient) < 0.01,
            $"Expected near-zero slope for constant %-returns, got {result.SlopeCoefficient:F6}");
    }

    [Fact]
    public void ImprovingPnl_PositiveSlope()
    {
        // Returns improve over time
        var pnl = new double[200];
        for (var i = 0; i < 200; i++)
            pnl[i] = 1.0 + i * 0.15; // Starts at 1, ends at ~31

        var result = DecayAnalyzer.Analyze(pnl, 10000.0, rollingWindow: 30);

        Assert.False(result.IsDecaying, "Expected IsDecaying=false for improving P&L");
        Assert.True(result.SlopeCoefficient > 0,
            $"Expected positive slope, got {result.SlopeCoefficient:F6}");
    }

    [Fact]
    public void RollingSharpe_HasCorrectCount()
    {
        var pnl = new double[100];
        for (var i = 0; i < 100; i++) pnl[i] = i % 2 == 0 ? 10.0 : -3.0;

        var result = DecayAnalyzer.Analyze(pnl, 10000.0, rollingWindow: 20);

        // Should have 100 - 20 = 80 rolling Sharpe values
        Assert.Equal(80, result.RollingSharpe.Count);
        Assert.Equal(20, result.RollingSharpe[0].BarIndex);
        Assert.Equal(99, result.RollingSharpe[^1].BarIndex);
    }

    [Fact]
    public void TooFewBars_ReturnsDefault()
    {
        var pnl = new double[] { 1, 2, 3 };

        var result = DecayAnalyzer.Analyze(pnl, 10000.0, rollingWindow: 60);

        Assert.Empty(result.RollingSharpe);
        Assert.Equal(0.0, result.SlopeCoefficient);
        Assert.False(result.IsDecaying);
    }
}
