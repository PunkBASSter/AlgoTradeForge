using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class WindowMetricsCalculatorTests
{
    [Fact]
    public void FlatPnl_ReturnsZeroMetrics()
    {
        var pnl = new double[] { 0, 0, 0, 0, 0 };
        var result = WindowMetricsCalculator.Compute(pnl, 10000, 365);

        Assert.Equal(0, result.TotalReturnPct);
        Assert.Equal(0, result.AnnualizedReturnPct);
        Assert.Equal(0, result.SharpeRatio);
        Assert.Equal(0, result.MaxDrawdownPct);
        Assert.Equal(5, result.BarCount);
    }

    [Fact]
    public void AscendingEquity_PositiveReturn()
    {
        // Each bar gains 100 from initial 10000 → final 10500 → 5% return
        var pnl = new double[] { 100, 100, 100, 100, 100 };
        var result = WindowMetricsCalculator.Compute(pnl, 10000, 365);

        Assert.Equal(5.0, result.TotalReturnPct, 1);
        Assert.True(result.AnnualizedReturnPct > 0);
        Assert.True(result.SharpeRatio > 0);
        Assert.Equal(0, result.MaxDrawdownPct);
        Assert.Equal(double.MaxValue, result.ProfitFactor); // No losses
    }

    [Fact]
    public void KnownDrawdown_ComputedCorrectly()
    {
        // 10000 → 10500 → 10000 → 10500 → 10000 → 10500
        // Peak at 10500, trough at 10000 → DD = 500/10500 ≈ 4.76%
        var pnl = new double[] { 500, -500, 500, -500, 500 };
        var result = WindowMetricsCalculator.Compute(pnl, 10000, 365);

        Assert.True(result.MaxDrawdownPct > 4.0);
        Assert.True(result.MaxDrawdownPct < 5.0);
    }

    [Fact]
    public void SingleBar_HandledGracefully()
    {
        var pnl = new double[] { 100 };
        var result = WindowMetricsCalculator.Compute(pnl, 10000, 365);

        Assert.Equal(1.0, result.TotalReturnPct, 1);
        Assert.Equal(0, result.SharpeRatio); // Can't compute Sharpe with 1 bar
        Assert.Equal(1, result.BarCount);
    }

    [Fact]
    public void EmptyPnl_ReturnsZeroMetrics()
    {
        var result = WindowMetricsCalculator.Compute(ReadOnlySpan<double>.Empty, 10000, 365);

        Assert.Equal(0, result.TotalReturnPct);
        Assert.Equal(0, result.BarCount);
    }

    [Fact]
    public void ZeroInitialEquity_ReturnsZeroMetrics()
    {
        var pnl = new double[] { 100, 200 };
        var result = WindowMetricsCalculator.Compute(pnl, 0, 365);

        Assert.Equal(0, result.TotalReturnPct);
    }

    [Fact]
    public void ProfitFactor_CorrectRatio()
    {
        // Sum positive = 300, Sum negative = 100 → PF = 3.0
        var pnl = new double[] { 100, -50, 200, -50 };
        var result = WindowMetricsCalculator.Compute(pnl, 10000, 365);

        Assert.Equal(3.0, result.ProfitFactor, 1);
    }

    [Fact]
    public void WindowFitnessEvaluator_PositiveMetrics_PositiveFitness()
    {
        var pnl = new double[] { 100, 100, 100, 100, 100, 100, 100, 100, 100, 100 };
        var metrics = WindowMetricsCalculator.Compute(pnl, 10000, 365);
        var fitness = WindowFitnessEvaluator.Evaluate(metrics);

        Assert.True(fitness > 0);
    }

    [Fact]
    public void WindowFitnessEvaluator_ZeroBars_ReturnsMinValue()
    {
        var metrics = WindowMetricsCalculator.Compute(ReadOnlySpan<double>.Empty, 10000, 365);
        var fitness = WindowFitnessEvaluator.Evaluate(metrics);

        Assert.Equal(double.MinValue, fitness);
    }
}
