using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation.Accumulators;

/// <summary>P5-4 / P5-6 — Range bar accumulator (TRD §6.3, ADR P5-1 D2).</summary>
public sealed class RangeAccumulatorTests
{
    private static SourceRecord Rec(long ts, long o, long h, long l, long c, long v) =>
        new(ts, o, h, l, c, v);

    /// <summary>Tick-style record: H = L = C = price (single instantaneous trade).</summary>
    private static SourceRecord Tick(long ts, long price, long qty) =>
        new(ts, price, price, price, price, qty);

    [Fact]
    public void TryAdvance_TickSpreadBelowThreshold_NoEmit()
    {
        var acc = new RangeAccumulator(threshold: 10);

        // Range walks 100..104..98..103: max spread = 6 < threshold.
        Assert.False(acc.TryAdvance(Tick(1000, 100, 5), out _));
        Assert.False(acc.TryAdvance(Tick(1100, 104, 5), out _));
        Assert.False(acc.TryAdvance(Tick(1200, 98, 5), out _));
        Assert.False(acc.TryAdvance(Tick(1300, 103, 5), out _));

        var stats = acc.Complete();
        Assert.Equal(0, stats.BarsEmitted);
    }

    [Fact]
    public void TryAdvance_TickSpreadCrossesThreshold_EmitsBar()
    {
        var acc = new RangeAccumulator(threshold: 10);

        // 100 → 105 (range 5) → 98 (range 7) → 110 (range 12 ≥ 10 → emit).
        Assert.False(acc.TryAdvance(Tick(1000, 100, 4), out _));
        Assert.False(acc.TryAdvance(Tick(1100, 105, 4), out _));
        Assert.False(acc.TryAdvance(Tick(1200, 98, 4), out _));
        Assert.True(acc.TryAdvance(Tick(1300, 110, 4), out var bar));

        Assert.Equal(1000, bar.TsMs);          // first tick's ts
        Assert.Equal(100, bar.Open);
        Assert.Equal(110, bar.High);           // running high after fourth tick
        Assert.Equal(98, bar.Low);
        Assert.Equal(110, bar.Close);
        Assert.Equal(16, bar.Volume);          // 4 + 4 + 4 + 4

        // Realized range = 12, threshold = 10 → overshoot 20%.
        var stats = acc.Complete();
        Assert.Equal(1, stats.BarsEmitted);
        Assert.Equal(20d, stats.MaxOvershootPct, 5);
        Assert.Equal(20d, stats.MeanOvershootPct, 5);
    }

    [Fact]
    public void TryAdvance_SingleRecordCrossesThreshold_EmitsImmediately()
    {
        // Time-bar-shaped record (H − L > threshold in one record). Phase 5 ships tick-only
        // at the eligibility layer, but the accumulator math itself is source-kind-agnostic.
        var acc = new RangeAccumulator(threshold: 10);

        var emitted = acc.TryAdvance(Rec(5000, 100, 130, 90, 120, 50), out var bar);

        Assert.True(emitted);
        Assert.Equal(40, bar.High - bar.Low);
        // Overshoot = (40 - 10) / 10 * 100 = 300%.
        var stats = acc.Complete();
        Assert.Equal(300d, stats.MaxOvershootPct, 5);
    }

    [Fact]
    public void TryAdvance_ExactThreshold_ZeroOvershoot()
    {
        var acc = new RangeAccumulator(threshold: 10);

        Assert.False(acc.TryAdvance(Tick(0, 100, 1), out _));
        Assert.False(acc.TryAdvance(Tick(1000, 105, 1), out _));
        Assert.True(acc.TryAdvance(Tick(2000, 110, 1), out var bar));

        Assert.Equal(10, bar.High - bar.Low);
        var stats = acc.Complete();
        Assert.Equal(0d, stats.MaxOvershootPct);
        Assert.Equal(0d, stats.MeanOvershootPct);
    }

    [Fact]
    public void TryAdvance_AfterEmit_StateResetsToNextRecord()
    {
        var acc = new RangeAccumulator(threshold: 10);

        // Bar 1: 100 → 110 emits at tick 2.
        acc.TryAdvance(Tick(1000, 100, 5), out _);
        Assert.True(acc.TryAdvance(Tick(2000, 110, 5), out var bar1));
        Assert.Equal(1000, bar1.TsMs);
        Assert.Equal(100, bar1.Open);

        // Bar 2: state reset — next record seeds open/high/low.
        Assert.False(acc.TryAdvance(Tick(3000, 112, 5), out _));
        Assert.False(acc.TryAdvance(Tick(4000, 115, 5), out _));
        Assert.True(acc.TryAdvance(Tick(5000, 122, 5), out var bar2));

        Assert.Equal(3000, bar2.TsMs);          // not 1000 — fresh state
        Assert.Equal(112, bar2.Open);
        Assert.Equal(122, bar2.High);
        Assert.Equal(112, bar2.Low);
        Assert.Equal(15, bar2.Volume);

        var stats = acc.Complete();
        Assert.Equal(2, stats.BarsEmitted);
    }

    [Fact]
    public void Finalize_TrailingPartialBar_Discarded()
    {
        var acc = new RangeAccumulator(threshold: 10);

        // One complete bar (range = 10), then trailing ticks with range < threshold.
        acc.TryAdvance(Tick(1000, 100, 1), out _);
        acc.TryAdvance(Tick(2000, 110, 1), out _);   // emits

        // Trailing — never crosses threshold.
        acc.TryAdvance(Tick(3000, 112, 1), out _);
        acc.TryAdvance(Tick(4000, 115, 1), out _);

        var stats = acc.Complete();
        Assert.Equal(1, stats.BarsEmitted);
    }

    [Fact]
    public void Constructor_NonPositiveThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RangeAccumulator(threshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RangeAccumulator(threshold: -1));
    }

    [Fact]
    public void TryAdvance_VolumeSumAcrossTicks_PreservedOnEmit()
    {
        var acc = new RangeAccumulator(threshold: 5);

        acc.TryAdvance(Tick(1000, 100, 7), out _);
        acc.TryAdvance(Tick(2000, 102, 11), out _);
        acc.TryAdvance(Tick(3000, 99, 13), out _);
        Assert.True(acc.TryAdvance(Tick(4000, 105, 17), out var bar));

        Assert.Equal(48, bar.Volume);     // 7 + 11 + 13 + 17
    }
}
