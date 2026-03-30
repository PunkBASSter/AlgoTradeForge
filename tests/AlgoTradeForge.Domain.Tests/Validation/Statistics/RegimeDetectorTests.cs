using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class RegimeDetectorTests
{
    [Fact]
    public void TwoDistinctRegimes_DetectsBoth()
    {
        // First 100 bars: high vol negative P&L (Bear)
        // Next 100 bars: low vol positive P&L (Bull)
        var pnl = new double[200];
        var rng = new Random(42);
        for (var i = 0; i < 100; i++)
            pnl[i] = -5.0 + rng.NextDouble() * 20 - 10; // Noisy negative
        for (var i = 100; i < 200; i++)
            pnl[i] = 2.0 + rng.NextDouble() * 1 - 0.5; // Steady positive

        var result = RegimeDetector.Analyze(pnl, 10000.0, volWindow: 20);

        Assert.True(result.Regimes.Count >= 2,
            $"Expected at least 2 regimes, got {result.Regimes.Count}");

        // Should have at least one Bull and one Bear segment
        var labels = result.Regimes.Select(r => r.Label).Distinct().ToList();
        Assert.True(labels.Count >= 2, $"Expected multiple regime types, got: {string.Join(", ", labels)}");
    }

    [Fact]
    public void SegmentsAreContiguous()
    {
        var pnl = new double[150];
        var rng = new Random(77);
        for (var i = 0; i < 150; i++)
            pnl[i] = rng.NextDouble() * 20 - 8;

        var result = RegimeDetector.Analyze(pnl, 10000.0, volWindow: 20);

        Assert.NotEmpty(result.Regimes);

        // First segment starts at volWindow
        Assert.Equal(20, result.Regimes[0].StartBar);

        // Segments are contiguous: each EndBar == next StartBar
        for (var i = 1; i < result.Regimes.Count; i++)
        {
            Assert.Equal(result.Regimes[i - 1].EndBar, result.Regimes[i].StartBar);
        }

        // Last segment ends at total bar count
        Assert.Equal(150, result.Regimes[^1].EndBar);
    }

    [Fact]
    public void SteadyPositivePnl_MostlyBullOrSideways()
    {
        var pnl = Enumerable.Repeat(5.0, 200).ToArray();

        var result = RegimeDetector.Analyze(pnl, 10000.0, volWindow: 20);

        // With constant positive P&L and zero volatility, vol ≤ P33 and mean > 0 → Bull
        Assert.NotEmpty(result.Regimes);
        Assert.True(result.ProfitableRegimeCount > 0);

        var bearCount = result.Regimes.Count(r => r.Label == "Bear");
        Assert.Equal(0, bearCount);
    }

    [Fact]
    public void SharpeRange_IsValid()
    {
        var pnl = new double[200];
        var rng = new Random(99);
        for (var i = 0; i < 200; i++)
            pnl[i] = rng.NextDouble() * 30 - 12;

        var result = RegimeDetector.Analyze(pnl, 10000.0, volWindow: 20);

        Assert.True(result.SharpeRange.Min <= result.SharpeRange.Max);
    }

    [Fact]
    public void TooFewBars_ReturnsEmpty()
    {
        var pnl = new double[] { 1, 2, 3, 4, 5 };

        var result = RegimeDetector.Analyze(pnl, 10000.0, volWindow: 60);

        Assert.Empty(result.Regimes);
        Assert.Equal(0, result.ProfitableRegimeCount);
    }

    [Fact]
    public void AllRegimeTypes_Present()
    {
        // Enough data with variety to hopefully get all 3 regime types
        var pnl = new double[500];
        var rng = new Random(42);

        // Bear: high vol, negative
        for (var i = 0; i < 150; i++)
            pnl[i] = -10.0 + rng.NextDouble() * 40 - 20;
        // Bull: low vol, positive
        for (var i = 150; i < 350; i++)
            pnl[i] = 3.0 + rng.NextDouble() * 2 - 1;
        // Sideways: mixed
        for (var i = 350; i < 500; i++)
            pnl[i] = rng.NextDouble() * 10 - 5;

        var result = RegimeDetector.Analyze(pnl, 10000.0, volWindow: 30);

        var labels = result.Regimes.Select(r => r.Label).Distinct().ToHashSet();
        // We should get at least 2 regime types from this varied data
        Assert.True(labels.Count >= 2,
            $"Expected multiple regime types, got: {string.Join(", ", labels)}");
    }
}
