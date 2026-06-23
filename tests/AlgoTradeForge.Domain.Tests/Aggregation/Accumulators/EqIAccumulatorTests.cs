using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.Aggregation.Accumulators;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation.Accumulators;

/// <summary>
/// P2b-1 / P2b-7 — equal-imbalance accumulator (TRD §6.3, §3.5). Same SourceRecord shape as
/// EqV/EqT/EqD, plus the new <see cref="SourceRecord.BuyVolumeLong"/> /
/// <see cref="SourceRecord.SellVolumeLong"/> fields. Sign convention is pinned by a 100%-buy
/// fixture (positive <c>signed_imbalance</c>) and a 100%-sell fixture (negative).
/// </summary>
public sealed class EqIAccumulatorTests
{
    // QuantityScale = 1/0.0001 = 10000 → 1.0 BTC = 10000 scaled long units.
    // Threshold 1000 means "abs signed scaled-long ≥ 1000" → 0.1 BTC equivalent.
    private static ScaleContext Scale() => new(tickSize: 0.01m, quantityStepSize: 0.0001m);

    private static SourceRecord Tick(long ts, long price, long qty, bool isBuy) => new(
        ts, price, price, price, price, qty,
        BuyVolumeLong: isBuy ? qty : 0L,
        SellVolumeLong: isBuy ? 0L : qty);

    [Fact]
    public void Constructor_NonPositiveThreshold_Throws()
    {
        var scale = Scale();
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqIVAccumulator(0, scale));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqIVAccumulator(-1, scale));
    }

    // ----- P2b-7 sign convention --------------------------------------------

    [Fact]
    public void TickPath_AllBuy_PositiveSignedImbalance_EmitsAtThreshold()
    {
        // Sign convention pinned: 100%-buy fixture. is_buyer_maker=0 → BuyVolumeLong = qty,
        // SellVolumeLong = 0. signed_acc = +qty cumulatively. Sidecar:
        //   buy_volume_raw = qty / QuantityScale  (back-converted to base-asset doubles)
        //   sell_volume_raw = 0
        //   signed_imbalance = buy - sell > 0
        var acc = new EqIVAccumulator(threshold: 1000, Scale());

        Assert.False(acc.TryAdvance(Tick(1000, 5_000_000, 400, isBuy: true), out _));
        Assert.False(acc.TryAdvance(Tick(2000, 5_000_010, 400, isBuy: true), out _));

        var emitted = acc.TryAdvance(Tick(3000, 5_000_020, 400, isBuy: true), out var bar);

        Assert.True(emitted);
        Assert.Equal(1000, bar.TsMs);
        Assert.Equal(1200, bar.Volume);    // sum of source qty

        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(1000, sidecar.TsMs);
        Assert.True(sidecar.SignedImbalance > 0d);          // 100%-buy → positive
        Assert.Equal(0d, sidecar.SellVolume);
        Assert.True(sidecar.BuyVolume > 0d);
        Assert.Equal(sidecar.BuyVolume, sidecar.SignedImbalance, 6);
        Assert.Equal(Math.Abs(sidecar.SignedImbalance), sidecar.RealizedThreshold, 6);
    }

    [Fact]
    public void TickPath_AllSell_NegativeSignedImbalance_EmitsAtThreshold()
    {
        // Mirror of the buy fixture: is_buyer_maker=1 → SellVolumeLong = qty.
        // signed_acc = -qty cumulatively → emits when abs ≥ threshold.
        var acc = new EqIVAccumulator(threshold: 1000, Scale());

        Assert.False(acc.TryAdvance(Tick(1000, 5_000_000, 400, isBuy: false), out _));
        Assert.False(acc.TryAdvance(Tick(2000, 5_000_010, 400, isBuy: false), out _));
        var emitted = acc.TryAdvance(Tick(3000, 5_000_020, 400, isBuy: false), out var bar);

        Assert.True(emitted);
        Assert.Equal(1000, bar.TsMs);

        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.True(sidecar.SignedImbalance < 0d);          // 100%-sell → negative
        Assert.Equal(0d, sidecar.BuyVolume);
        Assert.True(sidecar.SellVolume > 0d);
        Assert.Equal(-sidecar.SellVolume, sidecar.SignedImbalance, 6);
        Assert.Equal(Math.Abs(sidecar.SignedImbalance), sidecar.RealizedThreshold, 6);
    }

    [Fact]
    public void TickPath_MixedBuySell_SignedAccCancels_EmitsOnlyWhenAbsCrossesThreshold()
    {
        // Threshold 1000. Sub-threshold buys then a sell that pushes back below threshold,
        // then a final buy that crosses it. Sequence:
        //   +800 → signed_acc=+800   (no emit)
        //   -600 → signed_acc=+200   (no emit; net-buying not yet at threshold)
        //   +700 → signed_acc=+900   (no emit)
        //   +200 → signed_acc=+1100  → EMIT (overshoot 10%)
        // Sidecar after emit (back-converted via QuantityScale=10000):
        //   buy_volume   = (800 + 700 + 200) / 10000 = 0.17 BTC
        //   sell_volume  = 600 / 10000             = 0.06 BTC
        //   signed_imb   = 0.17 - 0.06             = 0.11
        //   realized_thr = |signed_imb|            = 0.11
        var acc = new EqIVAccumulator(threshold: 1000, Scale());

        Assert.False(acc.TryAdvance(Tick(1000, 100, 800, isBuy: true), out _));
        Assert.False(acc.TryAdvance(Tick(2000, 100, 600, isBuy: false), out _));
        Assert.False(acc.TryAdvance(Tick(3000, 100, 700, isBuy: true), out _));
        var emitted = acc.TryAdvance(Tick(4000, 100, 200, isBuy: true), out _);

        Assert.True(emitted);
        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(0.17, sidecar.BuyVolume, 6);
        Assert.Equal(0.06, sidecar.SellVolume, 6);
        Assert.Equal(0.11, sidecar.SignedImbalance, 6);
        Assert.Equal(0.11, sidecar.RealizedThreshold, 6);
    }

    [Fact]
    public void TickPath_AccumulatorResetsAfterEmit()
    {
        // After a bar emits, the accumulator must reset: a follow-up sub-threshold record does
        // NOT immediately emit again on top of the prior signed_acc carry-over.
        var acc = new EqIVAccumulator(threshold: 1000, Scale());

        Assert.True(acc.TryAdvance(Tick(0, 100, 1500, isBuy: true), out _));
        Assert.False(acc.TryAdvance(Tick(1000, 100, 100, isBuy: true), out _));    // 100 < 1000

        // Stats: one emit; mean_overshoot reflects (1500 - 1000)/1000 = 50%.
        var stats = acc.Complete();
        Assert.Equal(1, stats.BarsEmitted);
        Assert.Equal(50d, stats.MeanOvershootPct, 5);
        Assert.Equal(50d, stats.MaxOvershootPct, 5);
    }

    [Fact]
    public void TryGetLastSidecarRow_OneShotPerEmit()
    {
        // The pipeline reads the sidecar exactly once per TryAdvance emit. A second read with
        // no intervening emit must return false — otherwise the same sidecar row could leak into
        // a later partition's CSV (subtle bug in pipeline ordering refactors).
        var acc = new EqIVAccumulator(threshold: 1000, Scale());
        Assert.True(acc.TryAdvance(Tick(0, 100, 1500, isBuy: true), out _));
        Assert.True(acc.TryGetLastSidecarRow(out _));
        Assert.False(acc.TryGetLastSidecarRow(out _));    // already consumed
    }

    [Fact]
    public void TryGetLastSidecarRow_BeforeAnyEmit_ReturnsFalse()
    {
        var acc = new EqIVAccumulator(threshold: 1000, Scale());
        Assert.False(acc.TryGetLastSidecarRow(out _));
    }

    // ----- Time-bar proxy contribution --------------------------------------

    [Fact]
    public void TimeBarProxy_BuyVolumeAndSellVolumeFromCandleExt_BothNonZero()
    {
        // Time-bar proxy fixture: per-record contributions where Volume = total source vol
        // and BuyVolumeLong = ToLong(taker_buy_double * QuantityScale). Sell = Volume - Buy.
        // Thus signed_acc = Buy - Sell = 2*Buy - Volume, matching TRD §6.3 formula.
        var acc = new EqIVAccumulator(threshold: 600, Scale());

        // Bar 1: vol=400, taker_buy_long=300 → buy=300 sell=100 signed=+200
        // Bar 2: vol=400, taker_buy_long=350 → buy=350 sell=50  signed=+300; total +500 (no emit)
        // Bar 3: vol=400, taker_buy_long=400 → buy=400 sell=0   signed=+400; total +900 → emit
        Assert.False(acc.TryAdvance(new SourceRecord(1, 100, 110, 95, 105, 400, 300, 100), out _));
        Assert.False(acc.TryAdvance(new SourceRecord(2, 105, 115, 100, 110, 400, 350, 50), out _));
        var emitted = acc.TryAdvance(new SourceRecord(3, 110, 120, 105, 118, 400, 400, 0), out var bar);

        Assert.True(emitted);
        Assert.Equal(1, bar.TsMs);
        Assert.Equal(1200, bar.Volume);

        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(0.105, sidecar.BuyVolume, 6);          // (300+350+400)/10000
        Assert.Equal(0.015, sidecar.SellVolume, 6);         // (100+50+0)/10000
        Assert.Equal(0.090, sidecar.SignedImbalance, 6);
        Assert.Equal(0.090, sidecar.RealizedThreshold, 6);
    }
}
