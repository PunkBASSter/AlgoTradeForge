using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.Aggregation.Accumulators;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation.Accumulators;

/// <summary>P1b-2 / P1b-3 — equal-volume accumulator (TRD §6.3, §6.4).</summary>
public sealed class EqVAccumulatorTests
{
    private static SourceRecord Rec(long ts, long o, long h, long l, long c, long v) =>
        new(ts, o, h, l, c, v);

    [Fact]
    public void TryAdvance_VolumeAccumulatesUntilThreshold_EmitsBar()
    {
        var acc = new EqVAccumulator(threshold: 1000);

        // 3 records of vol=400 → cumulative 400, 800, 1200; emit on the third with overshoot=20%.
        Assert.False(acc.TryAdvance(Rec(1000, 100, 110, 95, 105, 400), out _));
        Assert.False(acc.TryAdvance(Rec(2000, 105, 115, 100, 112, 400), out _));

        var emitted = acc.TryAdvance(Rec(3000, 112, 120, 108, 118, 400), out var bar);

        Assert.True(emitted);
        Assert.Equal(1000, bar.TsMs);          // ts_open == first record
        Assert.Equal(100, bar.Open);
        Assert.Equal(120, bar.High);
        Assert.Equal(95, bar.Low);
        Assert.Equal(118, bar.Close);
        Assert.Equal(1200, bar.Volume);        // realized = 1200, overshoot 20%
    }

    [Fact]
    public void TryAdvance_SingleRecordCrossesThreshold_EmitsImmediately()
    {
        var acc = new EqVAccumulator(threshold: 1000);

        var emitted = acc.TryAdvance(Rec(5000, 100, 200, 90, 150, 5000), out var bar);

        Assert.True(emitted);
        Assert.Equal(5000, bar.Volume);
        // Overshoot = (5000 - 1000) / 1000 * 100 = 400%
        var stats = acc.Complete();
        Assert.Equal(1, stats.BarsEmitted);
        Assert.Equal(400d, stats.MaxOvershootPct, 5);
        Assert.Equal(400d, stats.MeanOvershootPct, 5);
    }

    [Fact]
    public void Finalize_TrailingPartialBar_Discarded()
    {
        var acc = new EqVAccumulator(threshold: 1000);

        // Two complete bars (1200 each, 20% overshoot), then one partial (400) discarded.
        for (var i = 0; i < 6; i++)
            acc.TryAdvance(Rec(i * 1000, 100, 110, 90, 105, 400), out _);
        acc.TryAdvance(Rec(7000, 100, 110, 90, 105, 400), out _);   // trailing, no emit

        var stats = acc.Complete();
        Assert.Equal(2, stats.BarsEmitted);
        Assert.Equal(20d, stats.MeanOvershootPct, 5);
        Assert.Equal(20d, stats.MaxOvershootPct, 5);
    }

    [Fact]
    public void TryAdvance_ExactThreshold_ZeroOvershoot()
    {
        var acc = new EqVAccumulator(threshold: 1000);

        Assert.False(acc.TryAdvance(Rec(0, 100, 110, 95, 105, 250), out _));
        Assert.False(acc.TryAdvance(Rec(1000, 105, 110, 100, 108, 250), out _));
        Assert.False(acc.TryAdvance(Rec(2000, 108, 110, 105, 109, 250), out _));
        Assert.True(acc.TryAdvance(Rec(3000, 109, 110, 105, 110, 250), out var bar));

        Assert.Equal(1000, bar.Volume);
        var stats = acc.Complete();
        Assert.Equal(0d, stats.MaxOvershootPct);
        Assert.Equal(0d, stats.MeanOvershootPct);
    }

    [Fact]
    public void Constructor_NonPositiveThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqVAccumulator(threshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqVAccumulator(threshold: -1));
    }
}
