namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Streaming accumulator contract. One instance per aggregation job; the source reader feeds
/// <see cref="SourceRecord"/>s in chronological order. <see cref="TryAdvance"/> returns
/// <c>true</c> + the emitted bar when the threshold is hit; otherwise the accumulator carries
/// internal state until the next call.
/// </summary>
public interface IBarAccumulator
{
    bool TryAdvance(in SourceRecord record, out AggregatedBar emitted);
    AggregationStats Finalize();

    /// <summary>
    /// EqI accumulators emit a sidecar row alongside each primary bar. The pipeline calls this
    /// immediately after a successful <see cref="TryAdvance"/> emit; non-EqI accumulators
    /// leave the default impl returning <c>false</c>.
    /// </summary>
    bool TryGetLastSidecarRow(out SidecarRow row)
    {
        row = default;
        return false;
    }

    /// <summary>
    /// Drain a queued bar from a path-dependent accumulator that emitted multiple bars from a
    /// single <see cref="TryAdvance"/> call (Renko). Default returns <c>false</c>; single-emit
    /// accumulators inherit the no-op.
    /// </summary>
    bool TryDrainQueued(out AggregatedBar emitted)
    {
        emitted = default;
        return false;
    }
}

/// <summary>
/// One row out of the source reader (a time-bar from <c>candles/</c> or a tick from
/// <c>ticks/</c>). All long-typed fields are tick-scaled per <see cref="AlgoTradeForge.Domain.ScaleContext"/>.
/// <c>BuyVolumeLong</c> / <c>SellVolumeLong</c> are populated only for EqI flows (0 otherwise).
/// </summary>
public readonly record struct SourceRecord(
    long TsMs,
    long Open,
    long High,
    long Low,
    long Close,
    long Volume,
    long BuyVolumeLong = 0L,
    long SellVolumeLong = 0L);

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

/// <summary>
/// Sidecar row emitted alongside an EqI bar. Side-feed convention: columns are <c>double</c>,
/// raw base-asset units (no scaling). <see cref="TsMs"/> joins 1:1 to the primary bar's
/// <c>ts</c>.
/// </summary>
public readonly record struct SidecarRow(
    long TsMs,
    double SignedImbalance,
    double BuyVolume,
    double SellVolume,
    double RealizedThreshold);

/// <summary>
/// Per-job aggregation stats returned by <see cref="IBarAccumulator.Finalize"/>.
/// <c>MonotonicBumps</c> counts equal-ts clusters bumped +1ms (benign at high volume);
/// <c>MonotonicRegressions</c> counts strictly out-of-order ticks recovered (indicates an
/// upstream ordering defect).
/// </summary>
public sealed record AggregationStats(
    long BarsEmitted,
    double MeanOvershootPct,
    double MaxOvershootPct,
    long MonotonicBumps = 0,
    long MonotonicRegressions = 0);

/// <summary>
/// Placeholder accumulator that emits no bars. Used when no real accumulator is wired.
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
