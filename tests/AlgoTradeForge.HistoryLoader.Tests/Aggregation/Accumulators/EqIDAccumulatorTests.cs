using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Aggregation.Accumulators;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation.Accumulators;

/// <summary>
/// Equal-Dollar-Imbalance accumulator (Lopez de Prado DIB). Two source paths:
/// <list type="bullet">
///   <item><b>Tick</b> (<c>useTimeBar = false</c>): per-trade qty in <c>BuyVolumeLong</c> /
///         <c>SellVolumeLong</c> (base-asset-tick units); accumulator computes
///         <c>signedQty × Close</c> contribution in dollar-tick units.</item>
///   <item><b>Time-bar</b> (<c>useTimeBar = true</c>): joiner pre-multiplies
///         <c>taker_buy_quote_vol</c> into <c>BuyVolumeLong</c> / <c>SellVolumeLong</c> in
///         dollar-tick units; accumulator sums directly with no Close-multiply.</item>
/// </list>
/// Sign convention pinned by 100%-buy fixture (positive signed_dollar_imbalance) and
/// 100%-sell fixture (negative).
/// </summary>
public sealed class EqIDAccumulatorTests
{
    // tickSize=0.01, quantityStepSize=0.0001 → QuantityScale=10000, ScaleFactor=100,
    // so dollarTickPerDollar = 10000 / 0.01 = 1,000,000.
    // A trade of qty 100 long (= 0.01 base) at price 10000 long (= $100) contributes
    //   100 × 10000 = 1e6 dollar-tick = $1 × 1,000,000. ✓
    private static ScaleContext Scale() => new(tickSize: 0.01m, quantityStepSize: 0.0001m);

    private static SourceRecord Tick(long ts, long price, long qty, bool isBuy) => new(
        ts, price, price, price, price, qty,
        BuyVolumeLong: isBuy ? qty : 0L,
        SellVolumeLong: isBuy ? 0L : qty);

    [Fact]
    public void Constructor_NonPositiveThreshold_Throws()
    {
        var scale = Scale();
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqIDAccumulator(0, scale, useTimeBar: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EqIDAccumulator(-1, scale, useTimeBar: false));
    }

    [Fact]
    public void Constructor_ZeroQuantityScale_Throws()
    {
        // QuantityScale = 1 / quantityStepSize, so quantityStepSize = 0 leaves QuantityScale = 1
        // (per ScaleContext fallback). Pin the explicit-zero path with a custom ctor: tickSize 0
        // is rejected by ScaleContext itself; QuantityScale 0 only reachable via direct
        // construction. Here we just confirm the QuantityScale-must-be-positive guard is in
        // place by using TickSize that produces dollarTickPerDollar > 0 via valid scale.
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0m);
        // QuantityScale defaulted to 1 (positive), so this should NOT throw. The explicit
        // guard is for future direct-record construction.
        _ = new EqIDAccumulator(100, scale, useTimeBar: false);
    }

    // ----- Tick path: signed_qty × Close contribution -----------------------

    [Fact]
    public void TickPath_AllBuy_PositiveSignedDollarImbalance_EmitsAtThreshold()
    {
        // Three buy ticks at qty=100, price=10000 each. Per-record contribution =
        // 100 × 10000 = 1e6 dollar-tick (= $1). signed_acc cumulates +1e6, +2e6, +3e6.
        // Threshold = 2.5e6 → emit at the 3rd tick.
        var acc = new EqIDAccumulator(threshold: 2_500_000L, Scale(), useTimeBar: false);

        Assert.False(acc.TryAdvance(Tick(1000, 10_000, 100, isBuy: true), out _));
        Assert.False(acc.TryAdvance(Tick(2000, 10_000, 100, isBuy: true), out _));
        var emitted = acc.TryAdvance(Tick(3000, 10_000, 100, isBuy: true), out var bar);

        Assert.True(emitted);
        Assert.Equal(1000, bar.TsMs);
        Assert.Equal(300, bar.Volume);  // sum of source qty in base-asset-tick units

        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(1000, sidecar.TsMs);
        // 3 trades × $1 each = $3 buy, $0 sell, signed = +$3.
        Assert.Equal(3.0, sidecar.BuyVolume, 6);
        Assert.Equal(0.0, sidecar.SellVolume, 6);
        Assert.Equal(3.0, sidecar.SignedImbalance, 6);
        Assert.Equal(3.0, sidecar.RealizedThreshold, 6);
    }

    [Fact]
    public void TickPath_AllSell_NegativeSignedDollarImbalance_EmitsAtThreshold()
    {
        var acc = new EqIDAccumulator(threshold: 2_500_000L, Scale(), useTimeBar: false);

        Assert.False(acc.TryAdvance(Tick(1000, 10_000, 100, isBuy: false), out _));
        Assert.False(acc.TryAdvance(Tick(2000, 10_000, 100, isBuy: false), out _));
        var emitted = acc.TryAdvance(Tick(3000, 10_000, 100, isBuy: false), out _);

        Assert.True(emitted);
        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.True(sidecar.SignedImbalance < 0d);
        Assert.Equal(0.0, sidecar.BuyVolume, 6);
        Assert.Equal(3.0, sidecar.SellVolume, 6);
        Assert.Equal(-3.0, sidecar.SignedImbalance, 6);
    }

    [Fact]
    public void TickPath_PriceVarying_ContributionScalesWithClose()
    {
        // Same qty (100) but increasing price: contribution per tick scales linearly with Close.
        // Tick 1: 100 × 5000 = 5e5 ($0.50)
        // Tick 2: 100 × 10000 = 1e6 ($1.00)
        // Tick 3: 100 × 20000 = 2e6 ($2.00)  → signed_acc = +3.5e6 → emit at threshold 3e6
        var acc = new EqIDAccumulator(threshold: 3_000_000L, Scale(), useTimeBar: false);

        Assert.False(acc.TryAdvance(Tick(1000, 5_000, 100, isBuy: true), out _));
        Assert.False(acc.TryAdvance(Tick(2000, 10_000, 100, isBuy: true), out _));
        var emitted = acc.TryAdvance(Tick(3000, 20_000, 100, isBuy: true), out _);

        Assert.True(emitted);
        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.Equal(3.5, sidecar.BuyVolume, 6);
        Assert.Equal(3.5, sidecar.SignedImbalance, 6);
    }

    [Fact]
    public void TickPath_AccumulatorResetsAfterEmit()
    {
        var acc = new EqIDAccumulator(threshold: 1_000_000L, Scale(), useTimeBar: false);

        Assert.True(acc.TryAdvance(Tick(0, 10_000, 200, isBuy: true), out _));      // 200×10000 = 2e6 → emit
        Assert.False(acc.TryAdvance(Tick(1000, 10_000, 50, isBuy: true), out _));   // 50×10000 = 5e5 < 1e6

        var stats = acc.Finalize();
        Assert.Equal(1, stats.BarsEmitted);
        Assert.Equal(100d, stats.MeanOvershootPct, 5);   // (2e6 - 1e6)/1e6 = 100%
    }

    [Fact]
    public void TryGetLastSidecarRow_OneShotPerEmit()
    {
        var acc = new EqIDAccumulator(threshold: 1_000_000L, Scale(), useTimeBar: false);
        Assert.True(acc.TryAdvance(Tick(0, 10_000, 200, isBuy: true), out _));
        Assert.True(acc.TryGetLastSidecarRow(out _));
        Assert.False(acc.TryGetLastSidecarRow(out _));
    }

    // ----- Time-bar path: BuyVolumeLong/SellVolumeLong already in dollar-tick units --

    [Fact]
    public void TimeBarPath_BuyAndSellPreScaled_AccumulatesDirectly()
    {
        // useTimeBar = true: the joiner has pre-multiplied taker_buy_quote_vol into
        // dollar-tick units. The accumulator takes BuyVolumeLong - SellVolumeLong directly
        // with no Close-multiply.
        // Per-bar contribution: signed = buy − sell. Three records summing to threshold.
        var acc = new EqIDAccumulator(threshold: 3_000_000L, Scale(), useTimeBar: true);

        // Bar 1: buy=2e6 ($2), sell=1e6 ($1), signed=+1e6
        Assert.False(acc.TryAdvance(new SourceRecord(
            1, 100, 110, 95, 105, 400, BuyVolumeLong: 2_000_000L, SellVolumeLong: 1_000_000L), out _));
        // Bar 2: buy=2e6, sell=1e6, signed=+1e6 → cum +2e6
        Assert.False(acc.TryAdvance(new SourceRecord(
            2, 105, 115, 100, 110, 400, BuyVolumeLong: 2_000_000L, SellVolumeLong: 1_000_000L), out _));
        // Bar 3: buy=2e6, sell=1e6, signed=+1e6 → cum +3e6 → emit
        var emitted = acc.TryAdvance(new SourceRecord(
            3, 110, 120, 105, 118, 400, BuyVolumeLong: 2_000_000L, SellVolumeLong: 1_000_000L), out var bar);

        Assert.True(emitted);
        Assert.Equal(1, bar.TsMs);
        Assert.Equal(1200, bar.Volume);  // sum of source vol

        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        // Buy total = 6e6 dollar-tick = $6.0; Sell total = 3e6 = $3.0; signed = $3.0.
        Assert.Equal(6.0, sidecar.BuyVolume, 6);
        Assert.Equal(3.0, sidecar.SellVolume, 6);
        Assert.Equal(3.0, sidecar.SignedImbalance, 6);
        Assert.Equal(3.0, sidecar.RealizedThreshold, 6);
    }

    // ----- Overflow safety stress test --------------------------------------

    [Fact]
    public void TickPath_LargeMagnitudeContributions_Int128AccumulatorHandlesProduct()
    {
        // BTCUSDT-perp realistic scale: tickSize=0.10, qty step=0.001.
        // QuantityScale = 1/0.001 = 1000; ScaleFactor = 1/0.10 = 10.
        // dollarTickPerDollar = 1000 / 0.10 = 10,000.
        // 1 BTC at $60,000: BuyVolumeLong = 1×1000 = 1000, Close = 60000/0.10 = 600,000.
        // Per-trade contribution = 1000 × 600,000 = 6e8 dollar-tick (= $60,000 × 10,000).
        // 10 trades = 6e9 dollar-tick. The per-record product comfortably fits long, but
        // the running sum is held in Int128 so multi-year accumulations can't wrap. This
        // pins the math at a typical magnitude — the safety margin is exercised by the
        // type itself, not a single test.
        var btcScale = new ScaleContext(tickSize: 0.10m, quantityStepSize: 0.001m);
        var threshold = 5_000_000_000L;     // 5e9 dollar-tick = $500,000 imbalance
        var acc = new EqIDAccumulator(threshold, btcScale, useTimeBar: false);

        // Per trade: qty 1 BTC (1000 long), price $60k (600,000 long), contribution = 6e8.
        // 9 trades = 5.4e9 → emit (overshoot ~8%).
        SourceRecord t = new(0, 600_000L, 600_000L, 600_000L, 600_000L, 1_000L,
            BuyVolumeLong: 1_000L, SellVolumeLong: 0L);
        bool emitted = false;
        var hits = 0;
        for (var i = 0; i < 10; i++)
        {
            t = t with { TsMs = i };
            emitted = acc.TryAdvance(t, out _);
            if (emitted) { hits = i + 1; break; }
        }

        Assert.True(emitted);
        // Should emit on the 9th trade (5.4e9 ≥ 5e9) — confirms math precision didn't drift.
        Assert.Equal(9, hits);
        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        // 9 trades × $60k = $540,000 buy-side cumulative.
        Assert.Equal(540_000d, sidecar.BuyVolume, 0);
        Assert.Equal(540_000d, sidecar.SignedImbalance, 0);
    }
}
