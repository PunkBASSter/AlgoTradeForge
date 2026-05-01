namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Streaming accumulator contract (TRD §6.2). One instance per aggregation job; the source
/// reader feeds <see cref="SourceRecord"/>s in chronological order. <see cref="TryAdvance"/>
/// returns <c>true</c> + the emitted bar when the threshold is hit; otherwise the
/// accumulator carries internal state until the next call.
/// </summary>
/// <remarks>
/// Phase 1a ships <see cref="NoOpBarAccumulator"/> only — the real EqT/EqV/EqD impls land in
/// Phase 1b, EqI in Phase 2b. The interface is fixed now so the scale-tag assertion at
/// accumulator entry has a stable signature to assert against.
/// </remarks>
public interface IBarAccumulator
{
    bool TryAdvance(in SourceRecord record, out AggregatedBar emitted);
    AggregationStats Finalize();
}

/// <summary>
/// One row out of the source reader (a time-bar from <c>candles/</c> or a tick from
/// <c>ticks/</c>). All long-typed fields are tick-scaled per <see cref="AlgoTradeForge.Domain.ScaleContext"/>.
/// </summary>
public readonly record struct SourceRecord(
    long TsMs,
    long Open,
    long High,
    long Low,
    long Close,
    long Volume);

/// <summary>
/// Aggregated bar output — same 6-long shape as <c>Int64Bar</c> for storage compatibility.
/// </summary>
public readonly record struct AggregatedBar(
    long TsMs,
    long Open,
    long High,
    long Low,
    long Close,
    long Volume);

/// <summary>Per-job aggregation stats returned by <see cref="IBarAccumulator.Finalize"/>.</summary>
/// <param name="BarsEmitted">Total number of <see cref="AggregatedBar"/>s emitted.</param>
/// <param name="MeanOvershootPct">Mean per-bar overshoot of the threshold accumulator at emission, in percent.</param>
/// <param name="MaxOvershootPct">Max per-bar overshoot of the threshold accumulator at emission, in percent.</param>
/// <param name="MonotonicBumps">
/// Phase 2a: count of source-side <c>+1 ms</c> timestamp bumps applied to enforce strict
/// monotonicity (TRD §6.3). Always 0 for time-bar sources; non-zero for tick sources whenever
/// multiple aggregated trades share a millisecond. Set by <c>AggregationPipeline</c> after
/// pipe-out from the accumulator (the bump is a property of the source stream, not the
/// accumulator math).
/// </param>
public sealed record AggregationStats(
    long BarsEmitted,
    double MeanOvershootPct,
    double MaxOvershootPct,
    long MonotonicBumps = 0);

/// <summary>
/// Phase 1a placeholder. The interface is wired through the pipeline so Phase 1b can
/// drop in the real <c>EqVAccumulator</c>, <c>EqTAccumulator</c>, etc. without further
/// plumbing. Calling <see cref="TryAdvance"/> always returns false (no bar emitted).
/// </summary>
public sealed class NoOpBarAccumulator : IBarAccumulator
{
    public bool TryAdvance(in SourceRecord record, out AggregatedBar emitted)
    {
        emitted = default;
        return false;
    }

    public AggregationStats Finalize() =>
        new(BarsEmitted: 0, MeanOvershootPct: 0d, MaxOvershootPct: 0d);
}
