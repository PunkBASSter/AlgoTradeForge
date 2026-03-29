using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class PermutationTesterTests
{
    [Fact]
    public void StrongTrend_LowPValue()
    {
        // Monotonically increasing P&L: ordering clearly matters
        // Start small, grow large — Sharpe depends on the compounding order
        var pnl = new double[200];
        for (var i = 0; i < 200; i++)
            pnl[i] = 1.0 + i * 0.5; // 1.0, 1.5, 2.0, ..., 100.5

        var result = PermutationTester.RunPnlPermutation(pnl, 10000.0, 500, seed: 42);

        Assert.Equal("PnlDelta", result.TestType);
        Assert.Equal(500, result.Iterations);
        Assert.True(result.PValue < 0.10,
            $"Expected low p-value for trending P&L, got {result.PValue:F3}");
    }

    [Fact]
    public void ConstantPnl_HighPValue()
    {
        // All identical deltas: shuffling changes nothing
        var pnl = Enumerable.Repeat(5.0, 100).ToArray();

        var result = PermutationTester.RunPnlPermutation(pnl, 10000.0, 200, seed: 42);

        // Every permutation is identical to original
        Assert.Equal(1.0, result.PValue);
        Assert.Equal(200, result.Iterations);
    }

    [Fact]
    public void IIDNoise_ModeratePValue()
    {
        // Random IID noise: ordering shouldn't matter much
        var rng = new Random(123);
        var pnl = new double[200];
        for (var i = 0; i < 200; i++)
            pnl[i] = rng.NextDouble() * 20 - 10; // Uniform [-10, 10]

        var result = PermutationTester.RunPnlPermutation(pnl, 10000.0, 500, seed: 42);

        // For IID data, p-value should be in the middle range (not near 0)
        Assert.True(result.PValue > 0.05,
            $"Expected moderate p-value for IID noise, got {result.PValue:F3}");
    }

    [Fact]
    public void DistributionLength_MatchesIterations()
    {
        var pnl = new double[] { 10, -5, 15, -3, 8, -2, 12, -4 };
        var result = PermutationTester.RunPnlPermutation(pnl, 1000.0, 300, seed: 42);

        Assert.Equal(300, result.PermutedDistribution.Length);
    }

    [Fact]
    public void Deterministic_SameSeed_SameResult()
    {
        var pnl = new double[50];
        var rng = new Random(55);
        for (var i = 0; i < 50; i++)
            pnl[i] = rng.NextDouble() * 30 - 10;

        var r1 = PermutationTester.RunPnlPermutation(pnl, 5000.0, 200, seed: 42);
        var r2 = PermutationTester.RunPnlPermutation(pnl, 5000.0, 200, seed: 42);

        Assert.Equal(r1.PValue, r2.PValue);
        Assert.Equal(r1.OriginalMetric, r2.OriginalMetric);
    }

    [Fact]
    public void ShortInput_ReturnsDefaultResult()
    {
        var result = PermutationTester.RunPnlPermutation(new double[] { 5.0 }, 1000.0, 100);

        Assert.Equal(1.0, result.PValue);
        Assert.Equal(0, result.Iterations);
        Assert.Empty(result.PermutedDistribution);
        Assert.Equal("PnlDelta", result.TestType);
    }

    [Fact]
    public void ComputeSharpe_KnownValues()
    {
        // 10 bars of constant +100 on 10000 initial → return = 100/equity each bar
        var pnl = Enumerable.Repeat(100.0, 10).ToArray();
        var sharpe = PermutationTester.ComputeSharpe(pnl, 10000.0, 1.0);

        // All returns are positive (slightly decreasing as equity grows), mean > 0, stdev small
        Assert.True(sharpe > 0, $"Expected positive Sharpe, got {sharpe:F4}");
    }
}
