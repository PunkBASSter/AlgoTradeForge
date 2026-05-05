using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Validates that source-feed and accumulator <see cref="ScaleContext"/>s match. Mismatched
/// scales would produce silently-wrong values from the long-arithmetic accumulators.
/// </summary>
public static class ScaleTagAssertion
{
    public static void Assert(ScaleContext source, ScaleContext accumulator)
    {
        if (source.TickSize != accumulator.TickSize)
        {
            throw new InvalidOperationException(
                $"Scale-tag mismatch: source.TickSize={source.TickSize}, " +
                $"accumulator.TickSize={accumulator.TickSize}. Aggregator inputs MUST share scale.");
        }

        if (source.QuantityScale != accumulator.QuantityScale)
        {
            throw new InvalidOperationException(
                $"Scale-tag mismatch: source.QuantityScale={source.QuantityScale}, " +
                $"accumulator.QuantityScale={accumulator.QuantityScale}. Aggregator inputs MUST share scale.");
        }
    }
}

/// <summary>
/// Single entry point for opening an accumulator. Asserts source/accumulator scale parity
/// before dispatching on <paramref name="typeCode"/>. Unknown type codes throw.
/// </summary>
public static class AccumulatorEntry
{
    public static IBarAccumulator Open(
        string typeCode,
        long threshold,
        ScaleContext sourceScale,
        ScaleContext accumulatorScale,
        DataFeedKind sourceKind = DataFeedKind.Tick)
    {
        ArgumentException.ThrowIfNullOrEmpty(typeCode);
        ScaleTagAssertion.Assert(sourceScale, accumulatorScale);

        // EqID/EqIT need the source kind to pick their contribution path: tick path computes
        // signed dollars / ±1; time-bar path consumes pre-aggregated quote-volume / trade
        // counts populated by CandleExtJoiningSource.
        var useTimeBar = sourceKind == DataFeedKind.TimeBar;

        return typeCode switch
        {
            "EqV" => new Accumulators.EqVAccumulator(threshold),
            "EqT" => new Accumulators.EqTAccumulator(threshold),
            "EqD" => new Accumulators.EqDAccumulator(threshold),
            "EqIV" => new Accumulators.EqIAccumulator(threshold, accumulatorScale),
            "EqID" => new Accumulators.EqIDAccumulator(threshold, accumulatorScale, useTimeBar),
            "EqIT" => new Accumulators.EqITAccumulator(threshold, useTimeBar),
            "Range" => new Accumulators.RangeAccumulator(threshold),
            "Renko" => new Accumulators.RenkoAccumulator(threshold),
            _ => throw new ArgumentException(
                $"Unknown alt-bar type code '{typeCode}' (allowed: EqT, EqV, EqD, EqIV, EqID, EqIT, Range, Renko).",
                nameof(typeCode)),
        };
    }
}
