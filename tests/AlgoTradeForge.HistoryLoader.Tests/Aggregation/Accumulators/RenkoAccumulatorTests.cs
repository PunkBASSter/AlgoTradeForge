using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation.Accumulators;

/// <summary>P5-7 / P5-9 — Renko bar accumulator (TRD §6.3, ADR P5-1 D3-D6).</summary>
public sealed class RenkoAccumulatorTests
{
    private static SourceRecord Tick(long ts, long price, long qty) =>
        new(ts, price, price, price, price, qty);

    [Fact]
    public void TryAdvance_FirstCall_SeedsWithoutEmitting()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        Assert.False(acc.TryAdvance(Tick(1000, 100, 5), out var bar));
        Assert.False(acc.TryDrainQueued(out _));

        var stats = acc.Complete();
        Assert.Equal(0, stats.BarsEmitted);
    }

    [Fact]
    public void TryAdvance_SingleBrickUp_EmitsCleanRectangle()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        Assert.False(acc.TryAdvance(Tick(1000, 100, 0), out _));     // seed (no pending vol)
        Assert.True(acc.TryAdvance(Tick(2000, 110, 7), out var bar));

        Assert.Equal(2000, bar.TsMs);
        Assert.Equal(100, bar.Open);
        Assert.Equal(110, bar.Close);
        Assert.Equal(110, bar.High);                 // max(open, close)
        Assert.Equal(100, bar.Low);                  // min(open, close) — no wick
        Assert.Equal(7, bar.Volume);

        Assert.False(acc.TryDrainQueued(out _));     // queue empty after single brick
    }

    [Fact]
    public void TryAdvance_SingleBrickDown_EmitsCleanRectangle()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        Assert.False(acc.TryAdvance(Tick(1000, 100, 0), out _));
        Assert.True(acc.TryAdvance(Tick(2000, 90, 5), out var bar));

        Assert.Equal(100, bar.Open);
        Assert.Equal(90, bar.Close);
        Assert.Equal(100, bar.High);                 // max(open, close)
        Assert.Equal(90, bar.Low);
        Assert.Equal(5, bar.Volume);
    }

    [Fact]
    public void TryAdvance_MultiBrickFromOneTick_FirstViaOut_RestViaQueue()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        Assert.False(acc.TryAdvance(Tick(1000, 100, 0), out _));     // seed
        // Tick 2: price moves 100 → 130 (3 bricks), vol = 30.
        Assert.True(acc.TryAdvance(Tick(2000, 130, 30), out var brick0));

        // Brick 0 — first via TryAdvance.
        Assert.Equal(2000, brick0.TsMs);
        Assert.Equal(100, brick0.Open);
        Assert.Equal(110, brick0.Close);
        Assert.Equal(10, brick0.Volume);             // pending(0) + perBrickVol(30/3 = 10)

        // Bricks 1..2 — queue drain in order, ts bumped +1 ms each.
        Assert.True(acc.TryDrainQueued(out var brick1));
        Assert.Equal(2001, brick1.TsMs);
        Assert.Equal(110, brick1.Open);
        Assert.Equal(120, brick1.Close);
        Assert.Equal(10, brick1.Volume);

        Assert.True(acc.TryDrainQueued(out var brick2));
        Assert.Equal(2002, brick2.TsMs);
        Assert.Equal(120, brick2.Open);
        Assert.Equal(130, brick2.Close);
        Assert.Equal(10, brick2.Volume);             // remainder = 30 - 2 * 10

        Assert.False(acc.TryDrainQueued(out _));
    }

    [Fact]
    public void TryAdvance_ReversalMidStream_HandlesDirectionChange()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        Assert.False(acc.TryAdvance(Tick(1000, 100, 0), out _));
        Assert.True(acc.TryAdvance(Tick(2000, 110, 5), out var up));    // up: 100 → 110

        // Reversal: lastBrickClose is now 110; tick at 100 → delta = -10, one down-brick.
        Assert.True(acc.TryAdvance(Tick(3000, 100, 7), out var down));
        Assert.Equal(110, down.Open);
        Assert.Equal(100, down.Close);
        Assert.Equal(110, down.High);
        Assert.Equal(100, down.Low);
        Assert.Equal(7, down.Volume);
    }

    [Fact]
    public void TryAdvance_DeltaBelowBrickSize_AccumulatesPendingVolume()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        Assert.False(acc.TryAdvance(Tick(1000, 100, 5), out _));        // seed; pending=5
        Assert.False(acc.TryAdvance(Tick(2000, 105, 3), out _));        // delta=5 < 10; pending=8
        Assert.False(acc.TryAdvance(Tick(3000, 95, 7), out _));         // delta=-5; pending=15

        // Now move to 110 (delta = 10 from lastBrickClose=100, one brick).
        Assert.True(acc.TryAdvance(Tick(4000, 110, 11), out var bar));

        Assert.Equal(100, bar.Open);
        Assert.Equal(110, bar.Close);
        Assert.Equal(26, bar.Volume);                // pending(15) + tick4 vol(11)
    }

    [Fact]
    public void Run_VolumeConservation_SumOfBricksEqualsSumOfTicks()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        // Sequence: seed + 2 no-emit + 1 multi-emit (3 bricks).
        var ticks = new[]
        {
            Tick(1000, 100, 5),
            Tick(2000, 102, 3),
            Tick(3000, 99, 4),
            Tick(4000, 130, 60),    // delta from 100 = 30, 3 bricks
        };

        long bricksTotal = 0;
        foreach (var t in ticks)
        {
            if (acc.TryAdvance(t, out var primary))
            {
                bricksTotal += primary.Volume;
                while (acc.TryDrainQueued(out var q))
                    bricksTotal += q.Volume;
            }
        }

        var ticksTotal = 5L + 3L + 4L + 60L;          // 72
        Assert.Equal(ticksTotal, bricksTotal);
    }

    [Fact]
    public void TryAdvance_MultiBrickWithPending_FirstBrickGetsPending()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        Assert.False(acc.TryAdvance(Tick(1000, 100, 5), out _));        // seed; pending=5
        Assert.False(acc.TryAdvance(Tick(2000, 102, 3), out _));        // pending=8
        // 3-brick chain, tick.vol = 30 → perBrick = 10, lastBrick = 10.
        Assert.True(acc.TryAdvance(Tick(3000, 130, 30), out var first));

        Assert.Equal(18, first.Volume);              // pending(8) + perBrickVol(10)

        Assert.True(acc.TryDrainQueued(out var mid));
        Assert.Equal(10, mid.Volume);

        Assert.True(acc.TryDrainQueued(out var last));
        Assert.Equal(10, last.Volume);
    }

    [Fact]
    public void TryAdvance_StrictMonotonicTs_BumpsSubsequentBricks()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        Assert.False(acc.TryAdvance(Tick(1000, 100, 0), out _));
        Assert.True(acc.TryAdvance(Tick(2000, 150, 50), out var first));    // 5 bricks

        var seenTs = new List<long> { first.TsMs };
        while (acc.TryDrainQueued(out var b))
            seenTs.Add(b.TsMs);

        Assert.Equal(5, seenTs.Count);
        // Strictly monotonic, starting at the trigger tick's ts.
        for (var i = 1; i < seenTs.Count; i++)
            Assert.True(seenTs[i] > seenTs[i - 1], $"ts[{i}]={seenTs[i]} should be > ts[{i - 1}]={seenTs[i - 1]}");

        // Next tick at ts=2003 should still emit at ts >= prev_emitted+1 = 2005.
        // Move 150 → 160 = one more brick.
        Assert.True(acc.TryAdvance(Tick(2003, 160, 8), out var afterBump));
        Assert.True(afterBump.TsMs > seenTs[^1]);
    }

    [Fact]
    public void TryAdvance_ExactBoundary_EmitsExactlyOneBrick()
    {
        var acc = new RenkoAccumulator(brickSize: 10);

        Assert.False(acc.TryAdvance(Tick(1000, 100, 0), out _));
        Assert.True(acc.TryAdvance(Tick(2000, 110, 5), out var bar));   // delta exactly 10

        Assert.Equal(110, bar.Close);
        Assert.False(acc.TryDrainQueued(out _));

        // After: lastBrickClose = 110. Another delta of exactly 10 → one more brick.
        Assert.True(acc.TryAdvance(Tick(3000, 120, 5), out var bar2));
        Assert.Equal(110, bar2.Open);
        Assert.Equal(120, bar2.Close);
    }

    [Fact]
    public void Constructor_NonPositiveBrickSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenkoAccumulator(brickSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenkoAccumulator(brickSize: -1));
    }

    [Fact]
    public void Finalize_TrailingPendingVolume_Discarded()
    {
        // Pin the conservation-invariant qualifier in the xmldoc/TRD: trailing pending volume
        // from final no-emit ticks is intentionally discarded at finalize (mirrors Range's
        // trailing partial-bar discard; §6.4 requires realized_threshold ≥ N to emit).
        var acc = new RenkoAccumulator(brickSize: 10);

        long bricksTotal = 0;
        // 3 ticks total. The third tick (qty=99) doesn't move price enough to emit and gets
        // accumulated into pending where it stays until finalize — its volume is lost.
        var ticks = new[]
        {
            Tick(1000, 100, 5),     // seed; pending=5
            Tick(2000, 110, 7),     // delta=10 → 1 brick (vol = 5 + 7 = 12); pending=0
            Tick(3000, 115, 99),    // delta=5 < 10 → no emit; pending=99 (DISCARDED at finalize)
        };
        foreach (var t in ticks)
        {
            if (acc.TryAdvance(t, out var primary))
            {
                bricksTotal += primary.Volume;
                while (acc.TryDrainQueued(out var q))
                    bricksTotal += q.Volume;
            }
        }

        var stats = acc.Complete();
        Assert.Equal(1, stats.BarsEmitted);
        Assert.Equal(12, bricksTotal);                // tick 1+2 vol consumed; tick 3's 99 lost
        var ticksTotal = 5L + 7L + 99L;               // 111
        Assert.NotEqual(ticksTotal, bricksTotal);     // explicit: invariant is "consumed", not "all"
    }

    [Fact]
    public void Finalize_OvershootStaysZero_ByConstruction()
    {
        // Each Renko brick is exactly brick_size tall — no overshoot semantics.
        var acc = new RenkoAccumulator(brickSize: 10);
        acc.TryAdvance(Tick(1000, 100, 0), out _);
        acc.TryAdvance(Tick(2000, 130, 30), out _);
        while (acc.TryDrainQueued(out _)) { }

        var stats = acc.Complete();
        Assert.Equal(3, stats.BarsEmitted);
        Assert.Equal(0d, stats.MeanOvershootPct);
        Assert.Equal(0d, stats.MaxOvershootPct);
    }
}
