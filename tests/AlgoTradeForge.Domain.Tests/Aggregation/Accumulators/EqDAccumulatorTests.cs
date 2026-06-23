using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.Aggregation.Accumulators;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation.Accumulators;

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
        var stats = acc.Complete();
        Assert.Equal(1, stats.BarsEmitted);
        Assert.Equal(10d, stats.MaxOvershootPct, 5);
        Assert.Equal(10d, stats.MeanOvershootPct, 5);
    }

    [Fact]
    public void Constructor_NonPositiveThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqDAccumulator(threshold: 0));
    }

    [Fact]
    public void TryAdvance_HighVolumePerp_DoesNotOverflowOnSingleRecord()
    {
        // Tick-scaled high-volume perp: ~$50k price × 10^5 scale = 5e9, qty 5e9.
        // Per-record product = 2.5e19, which overflows long (max ~9.22e18) but fits in Int128.
        // Before the Int128 widening, _thresholdAcc would wrap negative on the first record and
        // the threshold check would never fire.
        const long bigClose = 5_000_000_000L;
        const long bigVolume = 5_000_000_000L;
        const long threshold = 1_000_000_000_000_000_000L;   // 1e18

        var acc = new EqDAccumulator(threshold);
        var emitted = acc.TryAdvance(Rec(1000, bigClose, bigClose, bigClose, bigClose, bigVolume), out var bar);

        Assert.True(emitted, "Single-record product (2.5e19) is far above threshold (1e18); must emit.");
        Assert.Equal(1000, bar.TsMs);
        Assert.Equal(bigVolume, bar.Volume);
    }

    [Fact]
    public void TryAdvance_MultipleHighVolumeRecords_AccumulatesWithoutWrap()
    {
        // Three records each contributing ~3.3e18 (just under long.MaxValue/3 ≈ 3.07e18).
        // Cumulative sum is ~9.9e18, which would overflow long when summed but fits Int128.
        // Threshold at 9e18 ensures emission must happen on or before record 3 — pre-fix,
        // _thresholdAcc would wrap to negative on record 3's add and never trip the threshold.
        const long c = 1_000_000_000L;             // 1e9 close
        const long v = 3_300_000_000L;             // 3.3e9 volume → contribution ~3.3e18
        const long threshold = 9_000_000_000_000_000_000L;   // 9e18

        var acc = new EqDAccumulator(threshold);
        Assert.False(acc.TryAdvance(Rec(1000, c, c, c, c, v), out _));
        Assert.False(acc.TryAdvance(Rec(2000, c, c, c, c, v), out _));
        var emitted = acc.TryAdvance(Rec(3000, c, c, c, c, v), out var bar);

        Assert.True(emitted, "Cumulative ~9.9e18 must trip the 9e18 threshold; pre-fix overflow would prevent this.");
        Assert.Equal(1000, bar.TsMs);
        Assert.Equal(3 * v, bar.Volume);
    }
}
