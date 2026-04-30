using AlgoTradeForge.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Validates that the <see cref="ScaleContext"/> attached to a source feed matches the one
/// the accumulator is initialized with (TRD §3.4 scale-tag assertion). Phase 1a wires this
/// at the accumulator-entry call site even though Phase 1a's accumulator is a no-op — the
/// assertion locks the contract before Phase 1b adds real <c>long</c>-arithmetic accumulators
/// where a scale mismatch would produce silently-wrong values.
/// </summary>
public static class ScaleTagAssertion
{
    /// <summary>
    /// Throws when source / accumulator scales differ on <see cref="ScaleContext.TickSize"/>
    /// or <see cref="ScaleContext.QuantityScale"/>. Returns silently on match.
    /// </summary>
    public static void Assert(ScaleContext source, ScaleContext accumulator)
    {
        if (source.TickSize != accumulator.TickSize)
        {
            throw new InvalidOperationException(
                $"Scale-tag mismatch: source.TickSize={source.TickSize}, " +
                $"accumulator.TickSize={accumulator.TickSize}. " +
                $"Aggregator inputs MUST share scale (TRD §3.4).");
        }

        if (source.QuantityScale != accumulator.QuantityScale)
        {
            throw new InvalidOperationException(
                $"Scale-tag mismatch: source.QuantityScale={source.QuantityScale}, " +
                $"accumulator.QuantityScale={accumulator.QuantityScale}. " +
                $"Aggregator inputs MUST share scale (TRD §3.4).");
        }
    }
}

/// <summary>
/// Phase 1a wiring: scale-tag assertion fires at accumulator construction, even though the
/// accumulator we hand back is a no-op. Phase 1b replaces <see cref="NoOpBarAccumulator"/>
/// with the real type-specific accumulators without touching this entry point.
/// </summary>
public static class AccumulatorEntry
{
    public static IBarAccumulator Open(ScaleContext sourceScale, ScaleContext accumulatorScale)
    {
        ScaleTagAssertion.Assert(sourceScale, accumulatorScale);
        return new NoOpBarAccumulator();
    }
}
