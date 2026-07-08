using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

/// <summary>
/// Resume point for tick collection: the last <c>(agg_id, ts)</c> pair persisted to disk for
/// the latest daily partition. The collector advances its fetch cursor by <see cref="LastTsMs"/>
/// (re-fetches the boundary millisecond) and the writer dedups by <see cref="LastAggId"/>.
/// </summary>
public readonly record struct TickResumeState(long LastAggId, long LastTsMs);

/// <summary>
/// Daily-partitioned tick writer with <c>agg_id</c>-based dedup. Distinct from
/// <see cref="IFeedWriter"/> because ticks resume by <c>(aggId, ts)</c> (multiple trades commonly
/// share a millisecond) and partition by day, not month.
/// </summary>
public interface ITickFeedWriter
{
    /// <summary>
    /// Values must be <c>[price, qty, is_buyer_maker, agg_id]</c>. Price and qty are scaled to
    /// <c>long</c> by <c>10^decimalDigits</c> (Int64 Money Convention — same scale the reader parses).
    /// Records whose <c>agg_id</c> is at-or-below the partition watermark are silently dropped.
    /// </summary>
    void Write(string assetDir, FeedRecord record, int decimalDigits);

    Task<TickResumeState?> ResumeFrom(string assetDir, CancellationToken ct = default);
}
