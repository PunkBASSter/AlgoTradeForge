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
    AggregationStats Complete();

    /// <summary>
    /// Sidecar declaration. Non-null when the accumulator emits a sidecar row alongside each
    /// primary bar (EqIV, EqID, EqIT). The pipeline uses this to provision the sidecar staging
    /// dir, write the sidecar CSV header/rows, tag the manifest's fidelity reconstruction
    /// method, and pre-join candle-ext on time-bar sources. Default null = no sidecar.
    /// </summary>
    SidecarSchema? SidecarSchema => null;

    /// <summary>
    /// Imbalance accumulators populate this immediately after a successful <see cref="TryAdvance"/>
    /// emit. The pipeline reads it exactly once per emit; non-imbalance accumulators inherit
    /// the default false-returning impl.
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
/// <c>BuyVolumeLong</c> / <c>SellVolumeLong</c> are populated by the EqIV / EqID flows
/// (base-asset units for EqIV tick path and EqIV time-bar; quote-asset units for EqID).
/// <c>BuyTradeCountLong</c> / <c>SellTradeCountLong</c> are populated by the EqIT time-bar
/// flow only. All four imbalance fields default to 0 so non-imbalance accumulators ignore them.
/// </summary>
public readonly record struct SourceRecord(
    long TsMs,
    long Open,
    long High,
    long Low,
    long Close,
    long Volume,
    long BuyVolumeLong = 0L,
    long SellVolumeLong = 0L,
    long BuyTradeCountLong = 0L,
    long SellTradeCountLong = 0L);

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
/// Sidecar row emitted alongside an imbalance bar. Side-feed convention: columns are
/// <c>double</c>, raw units (no scaling). <see cref="TsMs"/> joins 1:1 to the primary bar's
/// <c>ts</c>.
/// <para>
/// Field names are generic; the per-type meaning is determined by the index into
/// <see cref="SidecarSchema.Columns"/>:
/// </para>
/// <list type="bullet">
///   <item>EqIV: buy/sell are base-asset volumes; signed = buy − sell.</item>
///   <item>EqID: buy/sell are quote-asset (dollar) volumes; signed = buy − sell.</item>
///   <item>EqIT: buy/sell are trade counts; signed = buy − sell counts.</item>
/// </list>
/// </summary>
public readonly record struct SidecarRow(
    long TsMs,
    double SignedImbalance,
    double BuyVolume,
    double SellVolume,
    double RealizedThreshold);

/// <summary>
/// Per-job aggregation stats returned by <see cref="IBarAccumulator.Complete"/>.
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

    public AggregationStats Complete() =>
        new(BarsEmitted: 0, MeanOvershootPct: 0d, MaxOvershootPct: 0d);
}

/// <summary>
/// Per-accumulator sidecar declaration. Owned by the accumulator (not the pipeline) so each
/// imbalance variant carries its own column shape and fidelity tags. The pipeline is
/// schema-agnostic: it reads <see cref="Header"/>, <see cref="Columns"/>, and the fidelity
/// tags off this record, and dispatches the candle-ext join based on
/// <see cref="TimeBarJoinMode"/>.
/// </summary>
/// <param name="Header">CSV header line written at the top of every sidecar partition.</param>
/// <param name="Columns">Column-name array stored in <c>feeds.json</c>'s sidecar entry.</param>
/// <param name="FidelityMethodTagTickSource">Manifest <c>imbalance_reconstruction_method</c> when the source is Tick.</param>
/// <param name="FidelityMethodTagTimeBarSource">Manifest <c>imbalance_reconstruction_method</c> when the source is TimeBar.</param>
/// <param name="TimeBarJoinMode">How the pipeline should populate <see cref="SourceRecord"/> from candle-ext for time-bar sources before feeding the accumulator.</param>
public sealed record SidecarSchema(
    string Header,
    IReadOnlyList<string> Columns,
    string FidelityMethodTagTickSource,
    string FidelityMethodTagTimeBarSource,
    CandleExtJoinMode TimeBarJoinMode);

/// <summary>
/// How the pipeline joins candle-ext into the source-record stream for time-bar sources.
/// Defines which extra column the joiner reads and which <see cref="SourceRecord"/> fields
/// it populates.
/// </summary>
public enum CandleExtJoinMode
{
    /// <summary>No candle-ext join (accumulator uses tick sources only or has no proxy).</summary>
    None,
    /// <summary>Read <c>taker_buy_vol</c> → <c>BuyVolumeLong</c>/<c>SellVolumeLong</c> in base-asset units (EqIV proxy).</summary>
    TakerBuyVolume,
    /// <summary>Read <c>taker_buy_quote_vol</c> → <c>BuyVolumeLong</c>/<c>SellVolumeLong</c> in quote-asset units (EqID proxy).</summary>
    TakerBuyQuoteVolume,
    /// <summary>Read <c>taker_buy_trade_count</c> + <c>trade_count</c> → <c>BuyTradeCountLong</c>/<c>SellTradeCountLong</c> (EqIT proxy).</summary>
    TakerBuyTradeCount,
}
