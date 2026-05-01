using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Aggregation;

/// <summary>
/// P1a-22, P1a-23 — scale-tag assertion at accumulator entry.
/// Phase 1a's "accumulator" is a no-op, but the assertion is wired so Phase 1b's real
/// accumulators inherit the contract.
/// </summary>
public sealed class ScaleTagAssertionTests
{
    [Fact]
    public void Assert_MatchingScales_DoesNotThrow()
    {
        var s1 = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var s2 = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);

        ScaleTagAssertion.Assert(s1, s2);
        // No throw.
    }

    [Fact]
    public void Assert_DifferentTickSize_Throws()
    {
        var s1 = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var s2 = new ScaleContext(tickSize: 0.001m, quantityStepSize: 0.0001m);

        var ex = Assert.Throws<InvalidOperationException>(() => ScaleTagAssertion.Assert(s1, s2));
        Assert.Contains("TickSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Assert_DifferentQuantityScale_Throws()
    {
        var s1 = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);   // QuantityScale = 10000
        var s2 = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.001m);    // QuantityScale = 1000

        var ex = Assert.Throws<InvalidOperationException>(() => ScaleTagAssertion.Assert(s1, s2));
        Assert.Contains("QuantityScale", ex.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // AccumulatorEntry — assertion fires at the accumulator-construction call site,
    // dispatch happens after assertion so a scale mismatch never reaches a real impl.
    // -------------------------------------------------------------------------

    [Fact]
    public void Open_MismatchedScales_ThrowsBeforeAllocatingAccumulator()
    {
        // A real EqV accumulator that scales `taker_buy_vol * QuantityScale` would silently
        // produce wrong long values if the source's QuantityScale differs from the
        // accumulator's expected scale. Asserting before dispatch makes the failure loud.
        var sourceScale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var accScale    = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.001m);

        Assert.Throws<InvalidOperationException>(() =>
            AccumulatorEntry.Open("EqV", threshold: 1000, sourceScale, accScale));
    }

    [Fact]
    public void Open_UnknownTypeCode_Throws()
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);

        Assert.Throws<ArgumentException>(() =>
            AccumulatorEntry.Open("WhatNow", threshold: 1000, scale, scale));
    }

    [Fact]
    public void Open_EqI_ReturnsEqIAccumulator()
    {
        // Phase 2b: EqI is now supported (P2b-1). The accumulator type is internal — test via
        // behavior, not type assertion: feed a 100%-buy fixture and confirm a sidecar row exists
        // after emission, which only EqIAccumulator produces.
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);

        var acc = AccumulatorEntry.Open("EqI", threshold: 1000, scale, scale);
        Assert.NotNull(acc);

        // Cross 1000 with one all-buy contribution; expect emission + sidecar row.
        var rec = new SourceRecord(0, 100, 110, 95, 105, 1500, BuyVolumeLong: 1500, SellVolumeLong: 0);
        Assert.True(acc.TryAdvance(in rec, out _));
        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.True(sidecar.SignedImbalance > 0d);   // sign convention: positive ⇒ buy-aggressive
    }

    [Fact]
    public void NoOpBarAccumulator_FinalizeReturnsZeroStats()
    {
        var acc = new NoOpBarAccumulator();

        var stats = acc.Finalize();

        Assert.Equal(0, stats.BarsEmitted);
        Assert.Equal(0d, stats.MeanOvershootPct);
        Assert.Equal(0d, stats.MaxOvershootPct);
    }
}
