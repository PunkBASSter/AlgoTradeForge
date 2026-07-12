namespace AlgoTradeForge.HistoryLoader.Application.Index;

public sealed record AssetIndexRow(string Exchange, string Dir, string Symbol, string Type, string ManifestJson);

public sealed record FeedStatusIndexRow(string Exchange, string Dir, string FeedName, string Interval, long? FirstTs, long? LastTs, long RecordCount, string Health, string GapsJson, string CompleteMonthsJson);

public sealed record MonthPartitionRow(string Month, long Rows, long FileLen, string FileMtimeUtc);

public sealed record IndexJobRow(
    string Id, string Kind, string State, string ProgressJson, string? Error,
    string? FeedKey, bool CancelRequested, string TouchedJson, string? RequestJson);

public sealed record JobEventRow(int Seq, string Kind, string PayloadJson, string CreatedAtUtc);

public sealed record InterruptedJobRow(string Id, string Kind, string? FeedKey, string TouchedJson);

public abstract record FeedGateOutcome
{
    public sealed record Acquired(string JobId) : FeedGateOutcome;
    public sealed record Busy(string ExistingJobId) : FeedGateOutcome;
}

public sealed record DiscoveredFirstMonthRow(string Exchange, string Dir, string FeedName, string Interval, string Month);

public sealed record InstrumentMetaRow(string Exchange, string Dir, int PriceDecimals, int QtyDecimals, string TickSize, string FetchedAtUtc);

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

    Task UpsertInstrumentMeta(IReadOnlyList<InstrumentMetaRow> rows, CancellationToken ct = default);
    Task<IReadOnlyList<InstrumentMetaRow>> ListInstrumentMeta(string? exchange = null, CancellationToken ct = default);

    Task SetDiscoveredFirstMonth(string exchange, string dir, string feedName, string interval, string month, CancellationToken ct = default);
    Task<IReadOnlyList<DiscoveredFirstMonthRow>> ListDiscoveredFirstMonths(CancellationToken ct = default);
    Task<IReadOnlyList<(string Exchange, string Dir, string FeedName, string Interval)>> ListAllFeedKeys(CancellationToken ct = default);

    Task PruneFeedData(string exchange, string dir,
        IReadOnlyCollection<(string FeedName, string Interval)> keep, CancellationToken ct = default);
    Task PruneAssetsNotIn(IReadOnlyCollection<(string Exchange, string Dir)> keep, CancellationToken ct = default);

    Task<bool> IsEmpty(CancellationToken ct = default);

    // Atomic create-and-claim for gated kinds (load/aggregation/materialize); requestJson persisted for rehydration.
    Task<FeedGateOutcome> TryAcquireFeedGate(string kind, string feedKey, string progressJson, string requestJson, CancellationToken ct = default);

    // Gateless create for index/catalog jobs (feed_key NULL, request_json NULL, state 'queued').
    Task<string> CreateJob(string kind, CancellationToken ct = default);
    Task UpdateJob(string id, string state, string? progressJson = null, string? error = null, CancellationToken ct = default);
    Task<IndexJobRow?> GetJob(string id, CancellationToken ct = default);
    Task<IReadOnlyList<IndexJobRow>> ListJobs(string? kind, string? state, CancellationToken ct = default);
    Task<IndexJobRow?> GetActiveJob(string kind, CancellationToken ct = default);
    /// <summary>Latest job of the kind regardless of state — bootstrap uses it to resume an interrupted rebuild.</summary>
    Task<IndexJobRow?> GetLastJob(string kind, CancellationToken ct = default);

    Task<int> AppendJobEvent(string jobId, string eventKind, string payloadJson, CancellationToken ct = default);
    Task<IReadOnlyList<JobEventRow>> GetJobEventsAfter(string jobId, int afterSeq, CancellationToken ct = default);
    Task<int> GetLastEventSeq(string jobId, CancellationToken ct = default);
}
