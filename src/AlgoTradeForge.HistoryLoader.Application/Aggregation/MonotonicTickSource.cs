using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Stateful decorator over a <see cref="SourceRecord"/> stream that enforces strict-monotonic
/// timestamps via <c>tsOut = max(prevTsOut + 1, raw.TsMs)</c> (TRD §6.3 / P2a-6).
/// </summary>
/// <remarks>
/// <para>
/// Tick sources can emit multiple aggregated trades sharing a millisecond. Downstream
/// <c>Int64Bar.TimestampMs</c> requires strictly increasing timestamps, so this decorator
/// nudges colliding ticks forward by 1 ms each within a cluster and counts the bumps for
/// fidelity reporting.
/// </para>
/// <para>
/// Two distinct anomalies are tracked separately because they have different root causes
/// and different remediation:
/// <list type="bullet">
///   <item><b><see cref="BumpCount"/></b> — equal-timestamp clusters (multiple aggregated
///         trades sharing the same exchange millisecond). Expected at high volume; benign.</item>
///   <item><b><see cref="RegressionCount"/></b> — strictly out-of-order records
///         (<c>raw.TsMs &lt; prev</c>). Indicates a real ingestor or pagination defect — the
///         decorator still recovers (forward-bumps the regressed record) but logs at WARN
///         level so the upstream bug surfaces in observability.</item>
/// </list>
/// </para>
/// <para>
/// Lives at the source layer (not the accumulator) so every accumulator type — EqT, EqV, EqD,
/// EqI (Phase 2b), Range/Renko (Phase 5) — sees a strictly-monotonic stream without
/// per-accumulator code. Single-pass; not thread-safe; one instance per job.
/// </para>
/// </remarks>
public sealed class MonotonicTickSource
{
    private readonly ILogger<MonotonicTickSource> _logger;

    public MonotonicTickSource() : this(NullLogger<MonotonicTickSource>.Instance) { }

    public MonotonicTickSource(ILogger<MonotonicTickSource> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Number of equal-timestamp clusters (raw.TsMs == prev) bumped forward during the most
    /// recent <see cref="Read"/> enumeration. Reset to <c>0</c> at the start of each
    /// enumeration; readable after iteration completes.
    /// </summary>
    public long BumpCount { get; private set; }

    /// <summary>
    /// Number of strictly out-of-order records (raw.TsMs &lt; prev) bumped forward during the
    /// most recent <see cref="Read"/> enumeration. A non-zero value indicates a real upstream
    /// ordering defect — the decorator recovers transparently but the count surfaces in
    /// fidelity stats so operators can detect ingestor regressions.
    /// </summary>
    public long RegressionCount { get; private set; }

    public IEnumerable<SourceRecord> Read(IEnumerable<SourceRecord> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        BumpCount = 0;
        RegressionCount = 0;

        // Two distinct "prev" values: the raw exchange ts of the previous record (used to
        // classify cluster-vs-regression — comparison is raw-vs-raw so an N-deep cluster
        // doesn't register as 1 cluster + (N-1) regressions after the first bump shifts the
        // bumped-prev forward), and the bumped tsOut floor (used to enforce strict monotonicity
        // on the emitted stream).
        long prevRawTs = long.MinValue;
        long prevBumpedTs = long.MinValue;
        foreach (var record in upstream)
        {
            long tsOut = record.TsMs;
            if (record.TsMs < prevRawTs)
            {
                _logger.LogWarning(
                    "Tick timestamp regression: raw={Raw} prev={Prev} delta={Delta}ms — bumping forward",
                    record.TsMs, prevRawTs, prevRawTs - record.TsMs);
                RegressionCount++;
                tsOut = prevBumpedTs + 1;
            }
            else if (record.TsMs == prevRawTs)
            {
                BumpCount++;
                tsOut = prevBumpedTs + 1;
            }
            else if (record.TsMs <= prevBumpedTs)
            {
                // Raw progressed (raw > prevRaw) but bumped floor is ahead of it — happens when
                // a long cluster bumped past this record's raw ts. Slot in just past the floor;
                // not a regression (raw didn't go backwards), not a cluster (no exact tie).
                tsOut = prevBumpedTs + 1;
            }

            prevRawTs = record.TsMs;
            prevBumpedTs = tsOut;

            yield return record with { TsMs = tsOut };
        }
    }
}
