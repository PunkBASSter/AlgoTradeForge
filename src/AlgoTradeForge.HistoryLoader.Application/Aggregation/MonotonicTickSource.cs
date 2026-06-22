using System.Runtime.CompilerServices;
using AlgoTradeForge.Domain.Aggregation;
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
    private long _prevRawTs;
    private long _prevBumpedTs;

    public MonotonicTickSource() : this(NullLogger<MonotonicTickSource>.Instance) { }

    public MonotonicTickSource(ILogger<MonotonicTickSource> logger)
    {
        _logger = logger;
    }

    public long BumpCount { get; private set; }
    public long RegressionCount { get; private set; }

    public IEnumerable<SourceRecord> Read(IEnumerable<SourceRecord> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ResetState();
        foreach (var record in upstream)
            yield return BumpOne(record);
    }

    public async IAsyncEnumerable<SourceRecord> Read(
        IAsyncEnumerable<SourceRecord> upstream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        ResetState();
        await foreach (var record in upstream.WithCancellation(ct))
            yield return BumpOne(record);
    }

    private void ResetState()
    {
        BumpCount = 0;
        RegressionCount = 0;
        _prevRawTs = long.MinValue;
        _prevBumpedTs = long.MinValue;
    }

    // Two "prev" values: _prevRawTs classifies cluster-vs-regression on raw-vs-raw (so an
    // N-deep cluster registers as N-1 cluster bumps, not 1 cluster + N-2 regressions);
    // _prevBumpedTs is the floor used to enforce strict monotonicity on the emitted stream.
    private SourceRecord BumpOne(SourceRecord record)
    {
        long tsOut = record.TsMs;
        if (record.TsMs < _prevRawTs)
        {
            _logger.LogWarning(
                "Tick timestamp regression: raw={Raw} prev={Prev} delta={Delta}ms — bumping forward",
                record.TsMs, _prevRawTs, _prevRawTs - record.TsMs);
            RegressionCount++;
            tsOut = _prevBumpedTs + 1;
        }
        else if (record.TsMs == _prevRawTs)
        {
            BumpCount++;
            tsOut = _prevBumpedTs + 1;
        }
        else if (record.TsMs <= _prevBumpedTs)
        {
            // Raw progressed but the bumped floor is ahead of it (a long cluster bumped past
            // this record's raw ts). Slot just past the floor; not a regression, not a cluster.
            tsOut = _prevBumpedTs + 1;
        }

        _prevRawTs = record.TsMs;
        _prevBumpedTs = tsOut;

        return record with { TsMs = tsOut };
    }
}
