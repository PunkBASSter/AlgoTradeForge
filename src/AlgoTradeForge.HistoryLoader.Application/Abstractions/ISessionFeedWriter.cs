using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

/// <summary>Last <c>ts</c> persisted for the latest daily <c>_session</c> partition.</summary>
public readonly record struct SessionResumeState(long LastTsMs);

/// <summary>
/// Daily-partitioned per-venue liveness writer (<c>{venueDir}/_session/&lt;YYYY-MM-DD&gt;.csv</c>,
/// schema <c>ts,kind</c>). Dedup by <c>ts</c> — heartbeats are emitted in monotonic time order.
/// </summary>
public interface ISessionFeedWriter
{
    /// <summary>Values must be <c>[kind]</c>. Records whose <c>ts</c> is at-or-below the
    /// partition watermark are silently dropped.</summary>
    void Write(string venueDir, FeedRecord record);

    Task<SessionResumeState?> ResumeFrom(string venueDir, CancellationToken ct = default);
}
