using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class ReturnSeriesAnalyzerTests
{
    [Fact]
    public void ComputeLogReturns_FlatEquity_AllZero()
    {
        ReadOnlySpan<double> equity = [100.0, 100.0, 100.0, 100.0];
        var returns = ReturnSeriesAnalyzer.ComputeLogReturns(equity);

        Assert.Equal(3, returns.Length);
        Assert.All(returns, r => Assert.Equal(0.0, r, precision: 10));
    }

    [Fact]
    public void ComputeLogReturns_TrendingUp_Positive()
    {
        ReadOnlySpan<double> equity = [100.0, 110.0, 121.0];
        var returns = ReturnSeriesAnalyzer.ComputeLogReturns(equity);

        Assert.Equal(2, returns.Length);
        // ln(110/100) ≈ 0.09531
        Assert.Equal(Math.Log(1.1), returns[0], precision: 8);
        // ln(121/110) ≈ 0.09531
        Assert.Equal(Math.Log(1.1), returns[1], precision: 8);
    }

    [Fact]
    public void ComputeLogReturns_SinglePoint_ReturnsEmpty()
    {
        ReadOnlySpan<double> equity = [100.0];
        var returns = ReturnSeriesAnalyzer.ComputeLogReturns(equity);
        Assert.Empty(returns);
    }

    [Fact]
    public void ComputeLogReturns_Empty_ReturnsEmpty()
    {
        ReadOnlySpan<double> equity = [];
        var returns = ReturnSeriesAnalyzer.ComputeLogReturns(equity);
        Assert.Empty(returns);
    }

    [Fact]
    public void ComputeLogReturns_ZeroEquity_ReturnsZeroForThatBar()
    {
        ReadOnlySpan<double> equity = [100.0, 0.0, 50.0];
        var returns = ReturnSeriesAnalyzer.ComputeLogReturns(equity);

        Assert.Equal(2, returns.Length);
        Assert.Equal(0.0, returns[0]); // equity[0] > 0 but equity[1] = 0
        Assert.Equal(0.0, returns[1]); // equity[1] = 0
    }

    [Fact]
    public void ComputeMoments_SymmetricReturns_ZeroSkewness()
    {
        ReadOnlySpan<double> returns = [-0.02, 0.02, -0.01, 0.01, -0.03, 0.03, -0.015, 0.015];

        var (skewness, _) = ReturnSeriesAnalyzer.ComputeMoments(returns);

        Assert.Equal(0.0, skewness, precision: 6);
    }

    [Fact]
    public void ComputeMoments_PositivelySkewed_PositiveSkewness()
    {
        // Returns with a few large positive outliers
        ReadOnlySpan<double> returns = [0.01, 0.01, 0.01, 0.01, 0.01, 0.01, 0.01, 0.20];

        var (skewness, _) = ReturnSeriesAnalyzer.ComputeMoments(returns);

        Assert.True(skewness > 0, $"Expected positive skewness but got {skewness}");
    }

    [Fact]
    public void ComputeMoments_NegativelySkewed_NegativeSkewness()
    {
        // Returns with a few large negative outliers
        ReadOnlySpan<double> returns = [-0.01, -0.01, -0.01, -0.01, -0.01, -0.01, -0.01, -0.20];

        var (skewness, _) = ReturnSeriesAnalyzer.ComputeMoments(returns);

        Assert.True(skewness < 0, $"Expected negative skewness but got {skewness}");
    }

    [Fact]
    public void ComputeMoments_NormalLike_ExcessKurtosisNearZero()
    {
        // A uniformly-spaced return series approximating normal tails
        ReadOnlySpan<double> returns =
        [
            -0.03, -0.02, -0.01, 0.0, 0.01, 0.02, 0.03,
            -0.03, -0.02, -0.01, 0.0, 0.01, 0.02, 0.03,
            -0.03, -0.02, -0.01, 0.0, 0.01, 0.02, 0.03,
        ];

        var (_, excessKurtosis) = ReturnSeriesAnalyzer.ComputeMoments(returns);

        // Uniform distribution has excess kurtosis ≈ -1.2
        Assert.True(Math.Abs(excessKurtosis) < 2.0,
            $"Expected excess kurtosis near 0 for uniform-like but got {excessKurtosis}");
    }

    [Fact]
    public void ComputeMoments_TooFewSamples_ReturnsZero()
    {
        ReadOnlySpan<double> returns = [0.01, 0.02, 0.03];

        var (skewness, excessKurtosis) = ReturnSeriesAnalyzer.ComputeMoments(returns);

        Assert.Equal(0.0, skewness);
        Assert.Equal(0.0, excessKurtosis);
    }
}
