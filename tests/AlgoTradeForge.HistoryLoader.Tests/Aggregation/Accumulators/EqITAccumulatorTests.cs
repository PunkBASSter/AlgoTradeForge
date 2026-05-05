using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation.Accumulators;

/// <summary>
/// Equal-Tick-count-Imbalance accumulator (Lopez de Prado TIB). Two source paths:
/// <list type="bullet">
///   <item><b>Tick</b> (<c>useTimeBar = false</c>): each tick contributes ±1 (sign of
///         <c>BuyVolumeLong − SellVolumeLong</c>). Tied/zero records contribute 0.</item>
///   <item><b>Time-bar</b> (<c>useTimeBar = true</c>): joiner populates
///         <c>BuyTradeCountLong</c> / <c>SellTradeCountLong</c> from
///         <c>taker_buy_trade_count</c> + <c>trade_count</c>. Per-record contribution is
///         the count delta directly.</item>
/// </list>
/// Counts are dimensionless — no scale conversion needed; sidecar columns hold raw counts.
/// </summary>
public sealed class EqITAccumulatorTests
{
    private static SourceRecord BuyTick(long ts, long price, long qty) => new(
        ts, price, price, price, price, qty, BuyVolumeLong: qty, SellVolumeLong: 0L);

    private static SourceRecord SellTick(long ts, long price, long qty) => new(
        ts, price, price, price, price, qty, BuyVolumeLong: 0L, SellVolumeLong: qty);

    [Fact]
    public void Constructor_NonPositiveThreshold_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqITAccumulator(0, useTimeBar: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqITAccumulator(-1, useTimeBar: false));
    }

    // ----- Tick path: ±1 per record -----------------------------------------

    [Fact]
    public void TickPath_AllBuy_PositiveSignedCount_EmitsAtThreshold()
    {
        // Threshold = 3 trade-count imbalance. Three buy ticks → signed = +3 → emit.
        var acc = new EqITAccumulator(threshold: 3, useTimeBar: false);

        Assert.False(acc.TryAdvance(BuyTick(1000, 100, 50), out _));
        Assert.False(acc.TryAdvance(BuyTick(2000, 100, 50), out _));
        var emitted = acc.TryAdvance(BuyTick(3000, 100, 50), out var bar);

        Assert.True(emitted);
        Assert.Equal(1000, bar.TsMs);
        Assert.Equal(150, bar.Volume);   // sum of source qty (preserved on bar.Volume)

        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(1000, sidecar.TsMs);
        Assert.Equal(3.0, sidecar.BuyVolume, 6);    // 3 buys
        Assert.Equal(0.0, sidecar.SellVolume, 6);
        Assert.Equal(3.0, sidecar.SignedImbalance, 6);
        Assert.Equal(3.0, sidecar.RealizedThreshold, 6);
    }

    [Fact]
    public void TickPath_AllSell_NegativeSignedCount_EmitsAtThreshold()
    {
        var acc = new EqITAccumulator(threshold: 3, useTimeBar: false);

        Assert.False(acc.TryAdvance(SellTick(1000, 100, 50), out _));
        Assert.False(acc.TryAdvance(SellTick(2000, 100, 50), out _));
        var emitted = acc.TryAdvance(SellTick(3000, 100, 50), out _);

        Assert.True(emitted);
        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(0.0, sidecar.BuyVolume, 6);
        Assert.Equal(3.0, sidecar.SellVolume, 6);
        Assert.Equal(-3.0, sidecar.SignedImbalance, 6);
    }

    [Fact]
    public void TickPath_MixedBuySell_CancelsCorrectly()
    {
        // Threshold 3. Sequence: +1, -1, +1 (cancel), +1, +1 (signed=+3 after 5 ticks → emit).
        var acc = new EqITAccumulator(threshold: 3, useTimeBar: false);

        Assert.False(acc.TryAdvance(BuyTick(1000, 100, 50), out _));    // +1
        Assert.False(acc.TryAdvance(SellTick(2000, 100, 50), out _));   // -1, signed=0
        Assert.False(acc.TryAdvance(BuyTick(3000, 100, 50), out _));    // +1
        Assert.False(acc.TryAdvance(BuyTick(4000, 100, 50), out _));    // +2
        var emitted = acc.TryAdvance(BuyTick(5000, 100, 50), out _);   // +3 → emit

        Assert.True(emitted);
        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(4.0, sidecar.BuyVolume, 6);    // 4 buys
        Assert.Equal(1.0, sidecar.SellVolume, 6);   // 1 sell
        Assert.Equal(3.0, sidecar.SignedImbalance, 6);
    }

    [Fact]
    public void TickPath_TiedRecord_ContributesZero()
    {
        // A tick with BuyVolumeLong == SellVolumeLong (degenerate / both flagged) MUST
        // contribute 0 — neither side gets credit. Subsequent buys still drive emit normally.
        var acc = new EqITAccumulator(threshold: 2, useTimeBar: false);

        // Tied record: BuyVolumeLong = SellVolumeLong = 50 (synthetic case).
        var tied = new SourceRecord(1000, 100, 100, 100, 100, 50,
            BuyVolumeLong: 50L, SellVolumeLong: 50L);
        Assert.False(acc.TryAdvance(tied, out _));     // 0 contribution
        Assert.False(acc.TryAdvance(BuyTick(2000, 100, 50), out _));    // +1
        var emitted = acc.TryAdvance(BuyTick(3000, 100, 50), out _);   // +2 → emit

        Assert.True(emitted);
        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(2.0, sidecar.BuyVolume, 6);    // tied tick excluded from buy count
        Assert.Equal(0.0, sidecar.SellVolume, 6);
    }

    [Fact]
    public void TickPath_AccumulatorResetsAfterEmit()
    {
        var acc = new EqITAccumulator(threshold: 2, useTimeBar: false);

        Assert.False(acc.TryAdvance(BuyTick(1000, 100, 50), out _));
        Assert.True(acc.TryAdvance(BuyTick(2000, 100, 50), out _));     // +2 → emit
        Assert.False(acc.TryAdvance(BuyTick(3000, 100, 50), out _));    // counter reset; +1 only

        var stats = acc.Complete();
        Assert.Equal(1, stats.BarsEmitted);
        Assert.Equal(0d, stats.MeanOvershootPct, 5);   // exactly hit threshold (no overshoot)
    }

    // ----- Time-bar path: pre-aggregated counts -----------------------------

    [Fact]
    public void TimeBarPath_BuyAndSellCountsFromCandleExt_SignedDeltaAccumulates()
    {
        // useTimeBar = true: per-record contribution is BuyTradeCountLong − SellTradeCountLong
        // (the joiner populates these from taker_buy_trade_count + trade_count).
        var acc = new EqITAccumulator(threshold: 30, useTimeBar: true);

        // Bar 1: 20 buys, 10 sells, signed=+10
        Assert.False(acc.TryAdvance(new SourceRecord(
            1, 100, 110, 95, 105, 400, BuyTradeCountLong: 20L, SellTradeCountLong: 10L), out _));
        // Bar 2: 25 buys, 10 sells, signed=+15 → cum +25
        Assert.False(acc.TryAdvance(new SourceRecord(
            2, 105, 115, 100, 110, 400, BuyTradeCountLong: 25L, SellTradeCountLong: 10L), out _));
        // Bar 3: 15 buys, 10 sells, signed=+5 → cum +30 → emit
        var emitted = acc.TryAdvance(new SourceRecord(
            3, 110, 120, 105, 118, 400, BuyTradeCountLong: 15L, SellTradeCountLong: 10L), out var bar);

        Assert.True(emitted);
        Assert.Equal(1, bar.TsMs);

        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(60.0, sidecar.BuyVolume, 6);   // 20 + 25 + 15
        Assert.Equal(30.0, sidecar.SellVolume, 6);  // 10 + 10 + 10
        Assert.Equal(30.0, sidecar.SignedImbalance, 6);
    }

    [Fact]
    public void TryGetLastSidecarRow_OneShotPerEmit()
    {
        var acc = new EqITAccumulator(threshold: 1, useTimeBar: false);
        Assert.True(acc.TryAdvance(BuyTick(0, 100, 50), out _));
        Assert.True(acc.TryGetLastSidecarRow(out _));
        Assert.False(acc.TryGetLastSidecarRow(out _));
    }
}
