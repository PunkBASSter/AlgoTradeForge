using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class SubPeriodAnalyzerTests
{
    [Fact]
    public void ConstantPositivePnl_HighR2_AllProfitable()
    {
        var pnl = Enumerable.Repeat(10.0, 100).ToArray();
        var result = SubPeriodAnalyzer.Analyze(pnl, 10000.0, numPeriods: 4);

        Assert.Equal(4, result.SubPeriods.Count);
        Assert.Equal(1.0, result.ProfitableSubPeriodsPct);
        Assert.True(result.EquityCurveR2 > 0.99,
            $"Expected R² near 1.0 for constant P&L, got {result.EquityCurveR2:F4}");
    }

    [Fact]
    public void FrontLoadedProfit_LowerR2()
    {
        // First half profitable, second half unprofitable
        var pnl = new double[100];
        for (var i = 0; i < 50; i++) pnl[i] = 20.0;
        for (var i = 50; i < 100; i++) pnl[i] = -15.0;

        var result = SubPeriodAnalyzer.Analyze(pnl, 10000.0, numPeriods: 4);

        Assert.Equal(4, result.SubPeriods.Count);
        Assert.True(result.ProfitableSubPeriodsPct < 1.0,
            $"Expected some unprofitable periods, got {result.ProfitableSubPeriodsPct:F2}");
        // R² should be lower than perfectly linear
        Assert.True(result.EquityCurveR2 < 0.95,
            $"Expected lower R² for front-loaded profit, got {result.EquityCurveR2:F4}");
    }

    [Fact]
    public void SubPeriods_CoverAllBars()
    {
        var pnl = new double[120];
        for (var i = 0; i < 120; i++) pnl[i] = i % 3 == 0 ? 5.0 : -1.0;

        var result = SubPeriodAnalyzer.Analyze(pnl, 10000.0, numPeriods: 6);

        Assert.Equal(6, result.SubPeriods.Count);
        Assert.Equal(0, result.SubPeriods[0].StartBar);
        Assert.Equal(120, result.SubPeriods[^1].EndBar);

        // Contiguous
        for (var i = 1; i < result.SubPeriods.Count; i++)
            Assert.Equal(result.SubPeriods[i - 1].EndBar, result.SubPeriods[i].StartBar);
    }

    [Fact]
    public void SharpeCoV_LowForConsistent()
    {
        // All periods have identical returns → CoV should be very low
        var pnl = Enumerable.Repeat(5.0, 200).ToArray();
        var result = SubPeriodAnalyzer.Analyze(pnl, 10000.0, numPeriods: 4);

        Assert.True(result.SharpeCoeffOfVariation < 0.5,
            $"Expected low Sharpe CoV for consistent returns, got {result.SharpeCoeffOfVariation:F3}");
    }

    [Fact]
    public void SinglePeriod_EdgeCase()
    {
        var pnl = Enumerable.Repeat(10.0, 50).ToArray();
        var result = SubPeriodAnalyzer.Analyze(pnl, 10000.0, numPeriods: 1);

        Assert.Single(result.SubPeriods);
        Assert.Equal(0, result.SubPeriods[0].StartBar);
        Assert.Equal(50, result.SubPeriods[0].EndBar);
    }

    [Fact]
    public void TooFewBars_ReturnsDefault()
    {
        var pnl = new double[] { 1, 2, 3 };
        var result = SubPeriodAnalyzer.Analyze(pnl, 10000.0, numPeriods: 10);

        Assert.Empty(result.SubPeriods);
        Assert.Equal(0.0, result.ProfitableSubPeriodsPct);
    }

    [Fact]
    public void EquityCurveR2_PerfectLinear()
    {
        // Constant P&L → perfectly linear equity curve
        var pnl = Enumerable.Repeat(1.0, 100).ToArray();
        var r2 = SubPeriodAnalyzer.ComputeEquityCurveR2(pnl, 1000.0);

        Assert.Equal(1.0, r2, precision: 10);
    }
}
