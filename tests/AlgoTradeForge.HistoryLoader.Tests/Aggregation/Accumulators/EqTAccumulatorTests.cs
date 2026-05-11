using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation.Accumulators;

/// <summary>P1b-4 / P1b-5 — equal-tick accumulator (TRD §6.3).</summary>
public sealed class EqTAccumulatorTests
{
    private static SourceRecord Rec(long ts, long o, long h, long l, long c, long v) =>
        new(ts, o, h, l, c, v);

    [Fact]
    public void TryAdvance_EmitsBarEveryNRecords_BaseVolumeSummed()
    {
        var acc = new EqTAccumulator(threshold: 5);

        for (var i = 0; i < 4; i++)
            Assert.False(acc.TryAdvance(Rec(i * 1000, 100, 110, 90, 105, 50), out _));

        var emitted = acc.TryAdvance(Rec(4000, 105, 120, 100, 115, 50), out var bar);

        Assert.True(emitted);
        Assert.Equal(0, bar.TsMs);
        Assert.Equal(100, bar.Open);
        Assert.Equal(120, bar.High);
        Assert.Equal(90, bar.Low);
        Assert.Equal(115, bar.Close);
        Assert.Equal(250, bar.Volume);    // 5 × 50, base volume regardless of count threshold
    }

    [Fact]
    public void Finalize_TwoBarsAndOneTrailingRecord_TrailingDiscarded()
    {
        var acc = new EqTAccumulator(threshold: 5);

        for (var i = 0; i < 11; i++)
            acc.TryAdvance(Rec(i * 1000, 100, 110, 90, 105, 50), out _);

        var stats = acc.Complete();
        Assert.Equal(2, stats.BarsEmitted);   // 5+5; the 11th is trailing
        Assert.Equal(0d, stats.MeanOvershootPct);   // EqT always lands on threshold exactly
        Assert.Equal(0d, stats.MaxOvershootPct);
    }

    [Fact]
    public void Constructor_NonPositiveThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqTAccumulator(threshold: 0));
    }
}
