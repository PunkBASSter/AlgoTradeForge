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
    // AccumulatorEntry — assertion fires at the accumulator-construction call site
    // -------------------------------------------------------------------------

    [Fact]
    public void Open_MatchingScales_ReturnsNoOpAccumulator()
    {
        var scale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);

        var acc = AccumulatorEntry.Open(scale, scale);

        Assert.IsType<NoOpBarAccumulator>(acc);

        // The no-op accumulator never emits.
        var emitted = acc.TryAdvance(new SourceRecord(0, 0, 0, 0, 0, 0), out _);
        Assert.False(emitted);
    }

    [Fact]
    public void Open_MismatchedScales_ThrowsBeforeAllocatingAccumulator()
    {
        // Phase 1a uses a no-op accumulator regardless, but the assertion is the gate —
        // a future EqV accumulator that scales `taker_buy_vol * QuantityScale` would silently
        // produce wrong long values if the source's QuantityScale differs from the
        // accumulator's expected scale.
        var sourceScale = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.0001m);
        var accScale    = new ScaleContext(tickSize: 0.01m, quantityStepSize: 0.001m);

        Assert.Throws<InvalidOperationException>(() => AccumulatorEntry.Open(sourceScale, accScale));
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
