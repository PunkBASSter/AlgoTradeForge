using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class MonteCarloBootstrapTests
{
    [Fact]
    public void ConstantPositivePnl_LowDrawdown()
    {
        var pnl = Enumerable.Repeat(10.0, 100).ToArray();
        var result = MonteCarloBootstrap.Run(pnl, 10000.0, 200, seed: 42);

        // Constant positive deltas: every shuffle is identical, so DD ≈ 0
        Assert.Equal(200, result.Iterations);
        Assert.True(result.DrawdownPercentiles[95] < 1.0,
            $"Expected near-zero DD for constant positive P&L, got P95={result.DrawdownPercentiles[95]:F2}%");
        Assert.Equal(0.0, result.ProbabilityOfRuin);
    }

    [Fact]
    public void AllNegativePnl_HighProbabilityOfRuin()
    {
        // Start with 100, lose 10 per bar for 20 bars → equity goes to -100
        var pnl = Enumerable.Repeat(-10.0, 20).ToArray();
        var result = MonteCarloBootstrap.Run(pnl, 100.0, 200, seed: 42);

        // All shuffles are identical (all -10), equity will always hit 0
        Assert.Equal(1.0, result.ProbabilityOfRuin);
        Assert.True(result.DrawdownPercentiles[95] > 90.0);
    }

    [Fact]
    public void AlternatingPnl_ModerateDrawdown()
    {
        // Alternating +50 / -30 → net positive but with drawdown potential
        var pnl = new double[100];
        for (var i = 0; i < 100; i++)
            pnl[i] = i % 2 == 0 ? 50.0 : -30.0;

        var result = MonteCarloBootstrap.Run(pnl, 10000.0, 500, seed: 42);

        Assert.Equal(500, result.Iterations);
        Assert.True(result.DrawdownPercentiles[50] > 0.0, "Median DD should be > 0 for alternating P&L");
        Assert.Equal(0.0, result.ProbabilityOfRuin); // Net positive, large initial equity
    }

    [Fact]
    public void PercentileOrdering_IsMonotonic()
    {
        var pnl = new double[200];
        var rng = new Random(123);
        for (var i = 0; i < 200; i++)
            pnl[i] = rng.NextDouble() * 20 - 8; // Slightly positive bias

        var result = MonteCarloBootstrap.Run(pnl, 5000.0, 500, seed: 42);

        Assert.True(result.DrawdownPercentiles[5] <= result.DrawdownPercentiles[25]);
        Assert.True(result.DrawdownPercentiles[25] <= result.DrawdownPercentiles[50]);
        Assert.True(result.DrawdownPercentiles[50] <= result.DrawdownPercentiles[75]);
        Assert.True(result.DrawdownPercentiles[75] <= result.DrawdownPercentiles[95]);
    }

    [Fact]
    public void FanBands_HasCorrectDimensions()
    {
        var pnl = Enumerable.Repeat(5.0, 50).ToArray();
        var result = MonteCarloBootstrap.Run(pnl, 1000.0, 100, seed: 42);

        Assert.Equal(5, result.EquityFanBands.Length); // 5 percentile bands
        foreach (var band in result.EquityFanBands)
            Assert.Equal(50, band.Length); // One value per bar
    }

    [Fact]
    public void FanBands_PercentilesOrdered_AtEachBar()
    {
        var pnl = new double[100];
        var rng = new Random(99);
        for (var i = 0; i < 100; i++)
            pnl[i] = rng.NextDouble() * 40 - 15;

        var result = MonteCarloBootstrap.Run(pnl, 5000.0, 300, seed: 42);

        // At each bar: P5 ≤ P25 ≤ P50 ≤ P75 ≤ P95
        for (var b = 0; b < 100; b++)
        {
            Assert.True(result.EquityFanBands[0][b] <= result.EquityFanBands[1][b],
                $"Bar {b}: P5 > P25");
            Assert.True(result.EquityFanBands[1][b] <= result.EquityFanBands[2][b],
                $"Bar {b}: P25 > P50");
            Assert.True(result.EquityFanBands[2][b] <= result.EquityFanBands[3][b],
                $"Bar {b}: P50 > P75");
            Assert.True(result.EquityFanBands[3][b] <= result.EquityFanBands[4][b],
                $"Bar {b}: P75 > P95");
        }
    }

    [Fact]
    public void Deterministic_SameSeed_SameResult()
    {
        var pnl = new double[80];
        var rng = new Random(77);
        for (var i = 0; i < 80; i++)
            pnl[i] = rng.NextDouble() * 30 - 10;

        var r1 = MonteCarloBootstrap.Run(pnl, 5000.0, 200, seed: 42);
        var r2 = MonteCarloBootstrap.Run(pnl, 5000.0, 200, seed: 42);

        Assert.Equal(r1.DrawdownPercentiles[95], r2.DrawdownPercentiles[95]);
        Assert.Equal(r1.ProbabilityOfRuin, r2.ProbabilityOfRuin);
    }

    [Fact]
    public void EmptyInput_ReturnsZeroResult()
    {
        var result = MonteCarloBootstrap.Run(ReadOnlySpan<double>.Empty, 10000.0, 100);

        Assert.Equal(0, result.Iterations);
        Assert.Equal(0.0, result.ProbabilityOfRuin);
        Assert.Empty(result.EquityFanBands);
    }
}
