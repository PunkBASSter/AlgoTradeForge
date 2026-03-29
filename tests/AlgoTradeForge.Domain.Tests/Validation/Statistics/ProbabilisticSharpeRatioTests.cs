using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class ProbabilisticSharpeRatioTests
{
    [Fact]
    public void StandardNormalCdf_ZeroReturnsHalf()
    {
        var result = ProbabilisticSharpeRatio.StandardNormalCdf(0.0);
        Assert.Equal(0.5, result, precision: 6);
    }

    [Fact]
    public void StandardNormalCdf_LargePositiveReturnsOne()
    {
        var result = ProbabilisticSharpeRatio.StandardNormalCdf(10.0);
        Assert.Equal(1.0, result, precision: 6);
    }

    [Fact]
    public void StandardNormalCdf_LargeNegativeReturnsZero()
    {
        var result = ProbabilisticSharpeRatio.StandardNormalCdf(-10.0);
        Assert.Equal(0.0, result, precision: 6);
    }

    [Fact]
    public void StandardNormalCdf_KnownValues()
    {
        // Phi(1) ≈ 0.8413
        Assert.Equal(0.8413, ProbabilisticSharpeRatio.StandardNormalCdf(1.0), precision: 3);
        // Phi(-1) ≈ 0.1587
        Assert.Equal(0.1587, ProbabilisticSharpeRatio.StandardNormalCdf(-1.0), precision: 3);
        // Phi(1.96) ≈ 0.975
        Assert.Equal(0.975, ProbabilisticSharpeRatio.StandardNormalCdf(1.96), precision: 2);
    }

    [Fact]
    public void StandardNormalQuantile_KnownValues()
    {
        // Phi_inv(0.5) = 0
        Assert.Equal(0.0, ProbabilisticSharpeRatio.StandardNormalQuantile(0.5), precision: 6);
        // Phi_inv(0.975) ≈ 1.96
        Assert.Equal(1.96, ProbabilisticSharpeRatio.StandardNormalQuantile(0.975), precision: 2);
        // Phi_inv(0.025) ≈ -1.96
        Assert.Equal(-1.96, ProbabilisticSharpeRatio.StandardNormalQuantile(0.025), precision: 2);
    }

    [Fact]
    public void StandardNormalQuantile_Extremes()
    {
        Assert.Equal(double.NegativeInfinity, ProbabilisticSharpeRatio.StandardNormalQuantile(0.0));
        Assert.Equal(double.PositiveInfinity, ProbabilisticSharpeRatio.StandardNormalQuantile(1.0));
    }

    [Fact]
    public void PSR_HighSharpe_ReturnsHighProbability()
    {
        // Observed Sharpe of 2.0, benchmark 0, 252 samples, normal distribution
        var psr = ProbabilisticSharpeRatio.ComputePSR(2.0, 0.0, 252, 0.0, 0.0);
        Assert.True(psr > 0.99);
    }

    [Fact]
    public void PSR_ZeroSharpe_ReturnsFiftyPercent()
    {
        // Observed = benchmark = 0
        var psr = ProbabilisticSharpeRatio.ComputePSR(0.0, 0.0, 252, 0.0, 0.0);
        Assert.Equal(0.5, psr, precision: 2);
    }

    [Fact]
    public void PSR_BelowBenchmark_ReturnsLowProbability()
    {
        var psr = ProbabilisticSharpeRatio.ComputePSR(0.3, 1.0, 252, 0.0, 0.0);
        Assert.True(psr < 0.05);
    }

    [Fact]
    public void PSR_NegativeSkewness_ReducesPSR()
    {
        // Use small sample size so the difference is visible at double precision
        var psrNormal = ProbabilisticSharpeRatio.ComputePSR(0.5, 0.0, 10, 0.0, 0.0);
        var psrSkewed = ProbabilisticSharpeRatio.ComputePSR(0.5, 0.0, 10, -2.0, 0.0);
        // Negative skewness increases the denominator, reducing z and thus PSR
        Assert.True(psrSkewed < psrNormal,
            $"Expected psrSkewed ({psrSkewed:F6}) < psrNormal ({psrNormal:F6})");
    }

    [Fact]
    public void DSR_SingleTrial_EqualsHighPSR()
    {
        // With 1 trial, expected max SR = 0, so DSR reduces to PSR(benchmark=0)
        var dsr = ProbabilisticSharpeRatio.ComputeDSR(1.5, 1, 252, 0.0, 0.0);
        var psr = ProbabilisticSharpeRatio.ComputePSR(1.5, 0.0, 252, 0.0, 0.0);
        Assert.Equal(psr, dsr, precision: 4);
    }

    [Fact]
    public void DSR_ManyTrials_DeflatesSharpe()
    {
        // Use a marginal Sharpe (0.3) so the deflation is visible
        // With many trials, the expected max SR rises, making 0.3 look worse
        var dsr1 = ProbabilisticSharpeRatio.ComputeDSR(0.3, 1, 50, 0.0, 0.0);
        var dsr10 = ProbabilisticSharpeRatio.ComputeDSR(0.3, 10, 50, 0.0, 0.0);
        var dsr100 = ProbabilisticSharpeRatio.ComputeDSR(0.3, 100, 50, 0.0, 0.0);

        Assert.True(dsr1 > dsr10,
            $"Expected dsr1 ({dsr1:F6}) > dsr10 ({dsr10:F6})");
        Assert.True(dsr10 > dsr100,
            $"Expected dsr10 ({dsr10:F6}) > dsr100 ({dsr100:F6})");
    }

    [Fact]
    public void DSR_SmallSample_ReturnsZero()
    {
        var dsr = ProbabilisticSharpeRatio.ComputeDSR(1.0, 10, 1, 0.0, 0.0);
        Assert.Equal(0.0, dsr);
    }

    [Fact]
    public void PSR_SmallSample_ReturnsZero()
    {
        var psr = ProbabilisticSharpeRatio.ComputePSR(1.0, 0.0, 1, 0.0, 0.0);
        Assert.Equal(0.0, psr);
    }
}
