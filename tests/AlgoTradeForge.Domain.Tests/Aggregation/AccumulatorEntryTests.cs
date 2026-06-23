using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Aggregation;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation;

// AccumulatorEntry.Open — the single accumulator factory. Scale parity is a precondition:
// a mismatch must throw before any accumulator is allocated (the long-arithmetic accumulators
// would otherwise produce silently-wrong values).
public sealed class AccumulatorEntryTests
{
    private static ScaleContext Scale(decimal tick = 0.01m, decimal step = 0.0001m) =>
        new(tickSize: tick, quantityStepSize: step);

    [Fact]
    public void Open_MismatchedTickSize_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AccumulatorEntry.Open("EqV", threshold: 1000, Scale(tick: 0.01m), Scale(tick: 0.001m)));
        Assert.Contains("TickSize", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_MismatchedQuantityScale_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AccumulatorEntry.Open("EqV", threshold: 1000, Scale(step: 0.0001m), Scale(step: 0.001m)));
        Assert.Contains("QuantityScale", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_UnknownTypeCode_Throws()
    {
        var scale = Scale();
        Assert.Throws<ArgumentException>(() =>
            AccumulatorEntry.Open("WhatNow", threshold: 1000, scale, scale));
    }

    [Fact]
    public void Open_EqIV_ProducesSidecarOnEmission()
    {
        // EqIV is sidecar-producing; opening with matching scales succeeds and a 100%-buy
        // contribution crossing the threshold emits a positive signed-imbalance sidecar row.
        var scale = Scale();
        var acc = AccumulatorEntry.Open("EqIV", threshold: 1000, scale, scale);
        Assert.NotNull(acc);

        var rec = new SourceRecord(0, 100, 110, 95, 105, 1500, BuyVolumeLong: 1500, SellVolumeLong: 0);
        Assert.True(acc.TryAdvance(in rec, out _));
        Assert.True(acc.TryGetLastSidecarRow(out var sidecar));
        Assert.True(sidecar.SignedImbalance > 0d);   // positive ⇒ buy-aggressive
    }

    [Fact]
    public void NoOpBarAccumulator_CompleteReturnsZeroStats()
    {
        var stats = new NoOpBarAccumulator().Complete();
        Assert.Equal(0, stats.BarsEmitted);
        Assert.Equal(0d, stats.MeanOvershootPct);
        Assert.Equal(0d, stats.MaxOvershootPct);
    }
}
