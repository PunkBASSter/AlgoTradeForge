using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Stateful decorator over a <see cref="SourceRecord"/> stream that enforces strict-monotonic
/// timestamps via <c>tsOut = max(prevTsOut + 1, raw.TsMs)</c>. Equal-ts clusters bump count
/// (benign at high volume); strict regressions log WARN and increment a separate count so
/// upstream ordering defects surface in observability. Single-pass; not thread-safe.
/// </summary>
public sealed class MonotonicTickSource
{
    private readonly ILogger<MonotonicTickSource> _logger;

    public MonotonicTickSource() : this(NullLogger<MonotonicTickSource>.Instance) { }

    public MonotonicTickSource(ILogger<MonotonicTickSource> logger)
    {
        _logger = logger;
    }

    /// <summary>Number of equal-timestamp clusters bumped forward during the most recent enumeration.</summary>
    public long BumpCount { get; private set; }

    /// <summary>Number of strictly out-of-order records bumped forward during the most recent enumeration.</summary>
    public long RegressionCount { get; private set; }

    public IEnumerable<SourceRecord> Read(IEnumerable<SourceRecord> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        BumpCount = 0;
        RegressionCount = 0;

        // Two "prev" values: prevRawTs classifies cluster-vs-regression on raw-vs-raw (so an
        // N-deep cluster registers as N-1 cluster bumps, not 1 cluster + N-2 regressions);
        // prevBumpedTs is the floor used to enforce strict monotonicity on the emitted stream.
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
                // Raw progressed but the bumped floor is ahead of it (a long cluster bumped past
                // this record's raw ts). Slot just past the floor; not a regression, not a cluster.
                tsOut = prevBumpedTs + 1;
            }

            prevRawTs = record.TsMs;
            prevBumpedTs = tsOut;

            yield return record with { TsMs = tsOut };
        }
    }
}
