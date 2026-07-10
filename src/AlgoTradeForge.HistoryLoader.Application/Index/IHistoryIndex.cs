namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed record AssetIndexRow(string Exchange, string Dir, string Symbol, string Type, string ManifestJson);

public sealed record FeedStatusIndexRow(string Exchange, string Dir, string FeedName, string Interval, long? FirstTs, long? LastTs, long RecordCount, string Health, string GapsJson, string CompleteMonthsJson);

public sealed record MonthPartitionRow(string Month, long Rows, long FileLen, string FileMtimeUtc);

public sealed record IndexJobRow(string Id, string Kind, string State, string ProgressJson, string? Error);

public interface IHistoryIndex
{
    Task UpsertAsset(AssetIndexRow row, CancellationToken ct = default);
    Task RemoveAsset(string exchange, string dir, CancellationToken ct = default);
    Task<IReadOnlyList<AssetIndexRow>> ListAssets(string? exchange = null, CancellationToken ct = default);
    Task<AssetIndexRow?> GetAsset(string exchange, string dir, CancellationToken ct = default);

    Task UpsertFeedStatus(FeedStatusIndexRow row, CancellationToken ct = default);
    Task<IReadOnlyList<FeedStatusIndexRow>> GetFeedStatuses(string exchange, string dir, CancellationToken ct = default);

    Task ReplaceMonths(string exchange, string dir, string feedName, string interval,
        IReadOnlyList<MonthPartitionRow> months, CancellationToken ct = default);
    Task<IReadOnlyList<MonthPartitionRow>> GetMonths(string exchange, string dir, string feedName, string interval, CancellationToken ct = default);

    /// <summary>Distinct (feed_name, interval) across feed_status AND month_partitions — feeds
    /// with month rows but no status row (static equity data) must not be invisible to sweeps.</summary>
    Task<IReadOnlyList<(string FeedName, string Interval)>> ListFeedKeys(string exchange, string dir, CancellationToken ct = default);

    Task PruneFeedData(string exchange, string dir,
        IReadOnlyCollection<(string FeedName, string Interval)> keep, CancellationToken ct = default);
    Task PruneAssetsNotIn(IReadOnlyCollection<(string Exchange, string Dir)> keep, CancellationToken ct = default);

    Task<bool> IsEmpty(CancellationToken ct = default);

    Task<string> CreateJob(string kind, CancellationToken ct = default);
    Task UpdateJob(string id, string state, string? progressJson = null, string? error = null, CancellationToken ct = default);
    Task<IndexJobRow?> GetJob(string id, CancellationToken ct = default);
    Task<IndexJobRow?> GetActiveJob(string kind, CancellationToken ct = default);
    /// <summary>Latest job of the kind regardless of state — bootstrap uses it to resume an interrupted rebuild.</summary>
    Task<IndexJobRow?> GetLastJob(string kind, CancellationToken ct = default);
}
