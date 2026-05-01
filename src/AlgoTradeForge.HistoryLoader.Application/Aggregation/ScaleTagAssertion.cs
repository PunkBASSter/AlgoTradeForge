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
/// Single entry point for opening an accumulator. Asserts source/accumulator scale parity
/// (TRD §3.4) before dispatching on <paramref name="typeCode"/>. Phase 1b ships EqV / EqT / EqD;
/// EqI lands in Phase 2b; Range / Renko in Phase 5. Unknown type codes throw.
/// </summary>
public static class AccumulatorEntry
{
    public static IBarAccumulator Open(
        string typeCode,
        long threshold,
        ScaleContext sourceScale,
        ScaleContext accumulatorScale)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeCode);
        ScaleTagAssertion.Assert(sourceScale, accumulatorScale);

        return typeCode switch
        {
            "EqV" => new Accumulators.EqVAccumulator(threshold),
            "EqT" => new Accumulators.EqTAccumulator(threshold),
            "EqD" => new Accumulators.EqDAccumulator(threshold),
            "EqI" => throw new NotSupportedException(
                "EqI accumulator lands in Phase 2b (signed-imbalance + .flow sidecar)."),
            "Range" or "Renko" => throw new NotSupportedException(
                $"{typeCode} accumulator lands in Phase 5."),
            _ => throw new ArgumentException(
                $"Unknown alt-bar type code '{typeCode}' (allowed: EqT, EqV, EqD, EqI, Range, Renko).",
                nameof(typeCode)),
        };
    }
}
