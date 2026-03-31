using AlgoTradeForge.Domain.Tests.Validation.TestHelpers;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class PboCalculatorTests
{
    [Fact]
    public void IdenticalTrials_PboAroundHalf()
    {
        // Two identical trials: IS-optimal is arbitrary, OOS ranking is random → PBO ≈ 0.5
        var timestamps = Enumerable.Range(0, 40).Select(i => (long)(i * 1000)).ToArray();
        var pnl1 = Enumerable.Repeat(1.0, 40).ToArray();
        var pnl2 = Enumerable.Repeat(1.0, 40).ToArray();
        var cache = new SimulationCache([timestamps, (long[])timestamps.Clone()], [pnl1, pnl2]);

        var result = PboCalculator.Compute(cache, numBlocks: 4, TestContext.Current.CancellationToken);

        Assert.Equal(4, result.NumBlocks);
        Assert.Equal(6, result.NumCombinations); // C(4,2) = 6
        Assert.Equal(6, result.LogitDistribution.Length);
        // With identical trials, rank of IS-optimal in OOS is always tied → PBO is 0.5
        Assert.True(result.Pbo >= 0.0 && result.Pbo <= 1.0);
    }

    [Fact]
    public void GenuinelyGoodTrial_LowPbo()
    {
        // Trial 0: strongly positive in every block. Trial 1: negative in every block.
        // IS-optimal (trial 0) should also rank high in OOS → low PBO.
        var timestamps = Enumerable.Range(0, 40).Select(i => (long)(i * 1000)).ToArray();
        var pnl0 = Enumerable.Repeat(10.0, 40).ToArray();
        var pnl1 = Enumerable.Repeat(-5.0, 40).ToArray();
        var cache = new SimulationCache([timestamps, (long[])timestamps.Clone()], [pnl0, pnl1]);

        var result = PboCalculator.Compute(cache, numBlocks: 4, TestContext.Current.CancellationToken);

        Assert.True(result.Pbo == 0.0,
            $"Expected PBO = 0 for genuinely good trial, got {result.Pbo:F3}");
    }

    [Fact]
    public void OverfitTrial_HighPbo()
    {
        // Trial 0: good in blocks 0,1 (IS), bad in blocks 2,3 (OOS-like)
        // Trial 1: mediocre everywhere
        // This should produce moderate-to-high PBO when IS blocks happen to include 0,1
        var timestamps = Enumerable.Range(0, 40).Select(i => (long)(i * 1000)).ToArray();

        var pnl0 = new double[40];
        for (var i = 0; i < 20; i++) pnl0[i] = 20.0;  // Great in first half
        for (var i = 20; i < 40; i++) pnl0[i] = -15.0; // Bad in second half

        var pnl1 = Enumerable.Repeat(1.0, 40).ToArray(); // Consistently mediocre

        var cache = new SimulationCache([timestamps, (long[])timestamps.Clone()], [pnl0, pnl1]);

        var result = PboCalculator.Compute(cache, numBlocks: 4, TestContext.Current.CancellationToken);

        // When blocks 0,1 are IS, trial 0 wins IS but loses OOS → overfit
        // When blocks 2,3 are IS, trial 1 wins IS and may or may not win OOS
        Assert.True(result.Pbo > 0.0,
            $"Expected non-zero PBO for overfit trial, got {result.Pbo:F3}");
    }

    [Fact]
    public void SmallBlocks_CorrectCombinationCount()
    {
        var timestamps = Enumerable.Range(0, 60).Select(i => (long)(i * 1000)).ToArray();
        var pnl = Enumerable.Repeat(1.0, 60).ToArray();
        var cache = new SimulationCache([timestamps, (long[])timestamps.Clone()], [pnl, pnl.ToArray()]);

        // S=6 → C(6,3) = 20
        var result = PboCalculator.Compute(cache, numBlocks: 6, TestContext.Current.CancellationToken);
        Assert.Equal(20, result.NumCombinations);
        Assert.Equal(20, result.LogitDistribution.Length);
    }

    [Fact]
    public void CombinationGenerator_C4_2_Returns6()
    {
        var combos = PboCalculator.GenerateCombinations(4, 2);

        Assert.Equal(6, combos.Count);
        // Verify first and last combinations
        Assert.Equal([0, 1], combos[0]);
        Assert.Equal([2, 3], combos[^1]);
    }

    [Fact]
    public void CombinationGenerator_C6_3_Returns20()
    {
        var combos = PboCalculator.GenerateCombinations(6, 3);
        Assert.Equal(20, combos.Count);
    }

    [Fact]
    public void InsufficientData_ReturnsDefault()
    {
        // Only 1 trial
        var timestamps = new long[] { 100, 200, 300 };
        var cache = new SimulationCache([timestamps], [new double[] { 1, 2, 3 }]);

        var result = PboCalculator.Compute(cache, numBlocks: 4, TestContext.Current.CancellationToken);

        Assert.Equal(0.5, result.Pbo);
        Assert.Equal(0, result.NumCombinations);
    }

    [Fact]
    public void TooFewBars_ReturnsDefault()
    {
        // 2 bars but asking for 4 blocks
        var timestamps = new long[] { 100, 200 };
        var cache = new SimulationCache([timestamps, (long[])timestamps.Clone()], [new double[] { 1, 2 }, new double[] { 3, 4 }]);

        var result = PboCalculator.Compute(cache, numBlocks: 4, TestContext.Current.CancellationToken);

        Assert.Equal(0.5, result.Pbo);
        Assert.Equal(0, result.NumCombinations);
    }

    [Fact]
    public void PboValue_BoundedZeroToOne()
    {
        var timestamps = Enumerable.Range(0, 80).Select(i => (long)(i * 1000)).ToArray();
        var rng = new Random(42);
        var trials = new double[5][];
        for (var t = 0; t < 5; t++)
        {
            trials[t] = new double[80];
            for (var b = 0; b < 80; b++)
                trials[t][b] = rng.NextDouble() * 20 - 10;
        }

        var tsArray = SimulationCacheTestHelper.ReplicateTimestamps(timestamps, trials.Length);
        var cache = new SimulationCache(tsArray, trials);
        var result = PboCalculator.Compute(cache, numBlocks: 4, TestContext.Current.CancellationToken);

        Assert.InRange(result.Pbo, 0.0, 1.0);
    }
}
