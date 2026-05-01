using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

/// <summary>
/// Resume point for tick collection: the last <c>(agg_id, ts)</c> pair successfully persisted
/// to disk for the latest daily partition. Returned by <see cref="ITickFeedWriter.ResumeFrom"/>;
/// the collector advances its Binance fetch cursor by <see cref="LastTsMs"/> (re-fetches the
/// boundary millisecond) and the writer dedups by <see cref="LastAggId"/>.
/// </summary>
public readonly record struct TickResumeState(long LastAggId, long LastTsMs);

/// <summary>
/// Daily-partitioned tick writer with <c>agg_id</c>-based dedup. Distinct from
/// <see cref="IFeedWriter"/> because (a) ticks need <c>(aggId, ts)</c> on resume, not just
/// <c>ts</c>, and (b) ticks partition by day, not by month. (TRD §3.5)
/// </summary>
public interface ITickFeedWriter
{
    /// <summary>
    /// Appends a tick record to the day-partition <c>{assetDir}/ticks/&lt;YYYY-MM-DD&gt;.csv</c>.
    /// The record's <c>FeedRecord.Values</c> must be <c>[price, qty, is_buyer_maker, agg_id]</c>.
    /// Records whose <c>agg_id</c> is at-or-below the cached last-written id for that day are
    /// silently dropped (resume-on-crash dedup).
    /// </summary>
    void Write(string assetDir, FeedRecord record);

    /// <summary>
    /// Reads the tail of the latest daily partition under <paramref name="assetDir"/> and
    /// returns the last successfully-written <c>(aggId, ts)</c> pair, or <c>null</c> if no
    /// partition exists. Repairs torn last-row writes by truncating the file to the last
    /// clean newline boundary before parsing.
    /// </summary>
    TickResumeState? ResumeFrom(string assetDir);
}
