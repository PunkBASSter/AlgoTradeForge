namespace AlgoTradeForge.HistoryLoader.Application.Index;

public interface IFeedMonthScanner
{
    /// <summary>
    /// Enumerates {yyyy-MM}_{interval}.csv partitions in feedDir. Rows are recounted only when
    /// (file_len, file_mtime) differ from the known row — unchanged files reuse the known count.
    /// Interval-less feeds have no month partitions to scan; callers skip them.
    /// </summary>
    Task<IReadOnlyList<MonthPartitionRow>> Scan(
        string feedDir, string interval,
        IReadOnlyDictionary<string, MonthPartitionRow> known,
        CancellationToken ct = default);
}
