using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

/// <summary>Last <c>(update_id, ts)</c> persisted for the latest daily partition.</summary>
public readonly record struct BookTickerResumeState(long LastUpdateId, long LastTsMs);

/// <summary>
/// Daily-partitioned best-bid/ask writer with <c>update_id</c>-based dedup, writing to
/// <c>{assetDir}/book-ticker/&lt;YYYY-MM-DD&gt;.csv</c>.
/// </summary>
public interface IBookTickerWriter
{
    /// <summary>
    /// Values must be <c>[bid_price, bid_qty, ask_price, ask_qty, update_id]</c>. Records whose
    /// <c>update_id</c> is at-or-below the partition watermark are silently dropped.
    /// </summary>
    void Write(string assetDir, FeedRecord record);

    Task<BookTickerResumeState?> ResumeFrom(string assetDir, CancellationToken ct = default);
}
