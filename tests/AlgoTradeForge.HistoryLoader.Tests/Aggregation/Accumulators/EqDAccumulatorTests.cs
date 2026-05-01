using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation.Accumulators;

/// <summary>
/// P1b-6 / P1b-7 — equal-dollar (quote-volume) accumulator (TRD §6.3).
/// Phase 1b approximates per-record quote-volume as <c>Close × Volume</c> when the source
/// is a time-bar feed (no <c>candle-ext</c> join yet — that's Phase 2b). The threshold is
/// in <c>tick × quant</c> units; the test feeds simple integer prices/volumes so the
/// arithmetic stays hand-computable.
/// </summary>
public sealed class EqDAccumulatorTests
{
    private static SourceRecord Rec(long ts, long o, long h, long l, long c, long v) =>
        new(ts, o, h, l, c, v);

    [Fact]
    public void TryAdvance_QuoteVolumeAccumulates_EmitsAtThreshold()
    {
        // threshold = 30,000 (e.g. close=100 × volume=300 cumulative across records)
        var acc = new EqDAccumulator(threshold: 30_000);

        // Record 1: close=100, vol=100 → contribution 10,000. Cumulative: 10,000.
        Assert.False(acc.TryAdvance(Rec(1000, 90, 105, 85, 100, 100), out _));

        // Record 2: close=110, vol=100 → contribution 11,000. Cumulative: 21,000.
        Assert.False(acc.TryAdvance(Rec(2000, 100, 115, 95, 110, 100), out _));

        // Record 3: close=120, vol=100 → contribution 12,000. Cumulative: 33,000 ≥ 30,000.
        var emitted = acc.TryAdvance(Rec(3000, 110, 125, 105, 120, 100), out var bar);

        Assert.True(emitted);
        Assert.Equal(1000, bar.TsMs);
        Assert.Equal(90, bar.Open);
        Assert.Equal(125, bar.High);
        Assert.Equal(85, bar.Low);
        Assert.Equal(120, bar.Close);
        Assert.Equal(300, bar.Volume);   // base volume sum (3 × 100), NOT quote volume

        // Overshoot = (33,000 - 30,000) / 30,000 * 100 = 10%
        var stats = acc.Finalize();
        Assert.Equal(1, stats.BarsEmitted);
        Assert.Equal(10d, stats.MaxOvershootPct, 5);
        Assert.Equal(10d, stats.MeanOvershootPct, 5);
    }

    [Fact]
    public void Constructor_NonPositiveThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqDAccumulator(threshold: 0));
    }
}
