using AlgoTradeForge.Domain.Aggregation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation;

/// <summary>
/// Phase 2a hidden task: P² streaming median estimator. Replaces the unbounded
/// <c>List&lt;long&gt;</c> on tick paths so 5y of perp ticks (~500M records) doesn't
/// allocate ~4 GB.
/// </summary>
public sealed class StreamingMedianEstimatorTests
{
    [Fact]
    public void Median_NoSamples_ReturnsZero()
    {
        var est = new StreamingMedianEstimator();
        Assert.Equal(0d, est.Median);
        Assert.Equal(0L, est.Count);
    }

    [Fact]
    public void Median_OneSample_ReturnsThatSample()
    {
        var est = new StreamingMedianEstimator();
        est.Add(42L);
        Assert.Equal(42d, est.Median);
        Assert.Equal(1L, est.Count);
    }

    [Fact]
    public void Median_FewSamples_ExactSortedMedian()
    {
        var est = new StreamingMedianEstimator();
        // Below 5 samples, falls back to exact sort+pick on the buffer.
        est.Add(10);
        est.Add(20);
        est.Add(30);
        Assert.Equal(20d, est.Median);  // exact

        est.Add(40);  // count=4, even → average of two middle
        Assert.Equal((20 + 30) / 2d, est.Median);
    }

    [Fact]
    public void Median_UniformSamples_WithinFivePercent()
    {
        // P² is approximate. For uniform [0..10000], true median = 5000.
        // Allow 5% tolerance — the 5-marker estimator typically does much better.
        var est = new StreamingMedianEstimator();
        var rng = new Random(Seed: 42);
        for (int i = 0; i < 10_000; i++)
            est.Add(rng.NextInt64(0, 10_001));

        Assert.InRange(est.Median, 5000d * 0.95, 5000d * 1.05);
    }

    [Fact]
    public void Median_SortedMonotonicInput_TracksTrueMedian()
    {
        // Easy case for P²: monotonically increasing input. After N samples,
        // true median = N/2 (approximately).
        var est = new StreamingMedianEstimator();
        for (long i = 1; i <= 1000; i++)
            est.Add(i);

        // True median = 500.5; approx tolerance of 2% on a small sample.
        Assert.InRange(est.Median, 500d * 0.98, 500d * 1.02);
    }

    [Fact]
    public void Median_ConstantInput_ReturnsConstant()
    {
        var est = new StreamingMedianEstimator();
        for (int i = 0; i < 1000; i++)
            est.Add(7L);

        Assert.Equal(7d, est.Median);
    }

    [Fact]
    public void Median_BimodalInput_LandsBetweenModes()
    {
        var est = new StreamingMedianEstimator();
        var rng = new Random(Seed: 17);
        // Half low (around 100), half high (around 1000). Median should be in between.
        for (int i = 0; i < 5000; i++)
            est.Add(95 + rng.NextInt64(0, 11));   // [95..105]
        for (int i = 0; i < 5000; i++)
            est.Add(995 + rng.NextInt64(0, 11));  // [995..1005]

        // True median is between the two modes — P² should land roughly mid-distribution.
        Assert.InRange(est.Median, 100d, 1000d);
    }

    [Fact]
    public void Count_TracksAddCalls()
    {
        var est = new StreamingMedianEstimator();
        for (int i = 0; i < 50; i++)
            est.Add(i);
        Assert.Equal(50L, est.Count);
    }
}
