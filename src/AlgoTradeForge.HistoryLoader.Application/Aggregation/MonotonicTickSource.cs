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
/// Lives at the source layer (not the accumulator) so every accumulator type — EqT, EqV, EqD,
/// EqI (Phase 2b), Range/Renko (Phase 5) — sees a strictly-monotonic stream without
/// per-accumulator code. Single-pass; not thread-safe; one instance per job.
/// </para>
/// </remarks>
public sealed class MonotonicTickSource
{
    /// <summary>
    /// Number of times the strict-monotonic rule overrode the raw exchange timestamp during
    /// the most recent <see cref="Read"/> enumeration. Reset to <c>0</c> at the start of each
    /// enumeration; readable after iteration completes.
    /// </summary>
    public long BumpCount { get; private set; }

    public IEnumerable<SourceRecord> Read(IEnumerable<SourceRecord> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        BumpCount = 0;

        long prevTs = long.MinValue;
        foreach (var record in upstream)
        {
            long tsOut = record.TsMs;
            if (tsOut <= prevTs)
            {
                tsOut = prevTs + 1;
                BumpCount++;
            }
            prevTs = tsOut;

            yield return record with { TsMs = tsOut };
        }
    }
}
