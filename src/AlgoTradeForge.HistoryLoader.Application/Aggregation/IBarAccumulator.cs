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

    /// <summary>
    /// EqI accumulators emit a sidecar row alongside each primary bar (TRD §3.5).
    /// The pipeline calls this immediately after a successful <see cref="TryAdvance"/> emit;
    /// non-EqI accumulators leave the default impl returning <c>false</c>.
    /// </summary>
    /// <remarks>
    /// Default-interface method: lives on <see cref="IBarAccumulator"/> rather than a separate
    /// <c>IFlowEmittingAccumulator</c> sub-interface so the pipeline's call site stays a single
    /// dispatch — the JIT inlines the default false-return for EqV/EqT/EqD. Per the P0-1 audit,
    /// DIM dispatch is safe across plugin assemblies on .NET 10.
    /// </remarks>
    bool TryGetLastSidecarRow(out SidecarRow row)
    {
        row = default;
        return false;
    }

    /// <summary>
    /// Phase 5 (Renko) — drain a queued bar from a path-dependent accumulator that emitted
    /// multiple bars from a single <see cref="TryAdvance"/> call. Returns <c>true</c> + the
    /// next queued bar; <c>false</c> when the queue is empty.
    /// </summary>
    /// <remarks>
    /// Default impl returns <c>false</c>. Single-emit accumulators (EqV/EqT/EqD/EqI/Range)
    /// inherit the no-op. Renko enqueues bricks 2..N internally — <see cref="TryAdvance"/>
    /// returns brick 1 via <c>out</c>, the pipeline drains the rest in a
    /// <c>while (acc.TryDrainQueued(out var b)) { ... }</c> loop. A drained bar carries no
    /// sidecar — sidecar emission is wired only after the primary <see cref="TryAdvance"/>
    /// emit (Phase 5 D7: Range/Renko have no sidecars regardless).
    /// </remarks>
    bool TryDrainQueued(out AggregatedBar emitted)
    {
        emitted = default;
        return false;
    }
}

/// <summary>
/// One row out of the source reader (a time-bar from <c>candles/</c> or a tick from
/// <c>ticks/</c>). All long-typed fields are tick-scaled per <see cref="AlgoTradeForge.Domain.ScaleContext"/>.
/// </summary>
/// <param name="TsMs">Bar/tick timestamp in epoch milliseconds.</param>
/// <param name="Open">Open price (tick-scaled).</param>
/// <param name="High">High price (tick-scaled).</param>
/// <param name="Low">Low price (tick-scaled).</param>
/// <param name="Close">Close price (tick-scaled).</param>
/// <param name="Volume">Base-asset volume (quantity-scaled).</param>
/// <param name="BuyVolumeLong">
/// Phase 2b (EqI): buy-aggressive volume contribution in the same scaled units as
/// <paramref name="Volume"/>. Tick path: <c>qty</c> when <c>is_buyer_maker == 0</c>, else 0.
/// Time-bar path (proxy): <c>MoneyConvert.ToLong(taker_buy_vol_double * QuantityScale)</c>.
/// 0 for non-EqI flows — EqV/EqT/EqD ignore this field.
/// </param>
/// <param name="SellVolumeLong">
/// Phase 2b (EqI): sell-aggressive volume contribution. Tick path: <c>qty</c> when
/// <c>is_buyer_maker == 1</c>, else 0. Time-bar path: <c>Volume - BuyVolumeLong</c>.
/// 0 for non-EqI flows.
/// </param>
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
/// Phase 2b — sidecar row emitted alongside an EqI bar (TRD §3.5). Side-feed convention:
/// columns are <c>double</c>, raw base-asset units (no scaling). The <see cref="TsMs"/>
/// field joins 1:1 to the primary bar's <c>ts</c>.
/// </summary>
/// <param name="TsMs">Bar open ts (joins to primary <c>ts</c>).</param>
/// <param name="SignedImbalance"><c>buy - sell</c> in raw base-asset units.</param>
/// <param name="BuyVolume">Cumulative buy-aggressive volume for the bar, raw base-asset units.</param>
/// <param name="SellVolume">Cumulative sell-aggressive volume for the bar, raw base-asset units.</param>
/// <param name="RealizedThreshold"><c>abs(SignedImbalance)</c> at emit (≥ N for EqI).</param>
public readonly record struct SidecarRow(
    long TsMs,
    double SignedImbalance,
    double BuyVolume,
    double SellVolume,
    double RealizedThreshold);

/// <summary>Per-job aggregation stats returned by <see cref="IBarAccumulator.Finalize"/>.</summary>
/// <param name="BarsEmitted">Total number of <see cref="AggregatedBar"/>s emitted.</param>
/// <param name="MeanOvershootPct">Mean per-bar overshoot of the threshold accumulator at emission, in percent.</param>
/// <param name="MaxOvershootPct">Max per-bar overshoot of the threshold accumulator at emission, in percent.</param>
/// <param name="MonotonicBumps">
/// Phase 2a: count of source-side <c>+1 ms</c> timestamp bumps applied to equal-timestamp
/// clusters (TRD §6.3). Always 0 for time-bar sources; non-zero for tick sources whenever
/// multiple aggregated trades share a millisecond. Benign — expected at high volume.
/// </param>
/// <param name="MonotonicRegressions">
/// Count of strictly out-of-order tick records (raw ts &lt; prev) the source decorator
/// recovered from. Non-zero indicates a real upstream ordering defect (ingestor bug,
/// pagination misorder); surfaced in fidelity stats so operators can detect it.
/// </param>
public sealed record AggregationStats(
    long BarsEmitted,
    double MeanOvershootPct,
    double MaxOvershootPct,
    long MonotonicBumps = 0,
    long MonotonicRegressions = 0);

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
