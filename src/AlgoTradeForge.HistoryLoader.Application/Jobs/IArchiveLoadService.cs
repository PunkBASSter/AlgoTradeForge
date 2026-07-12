using AlgoTradeForge.HistoryLoader.Application.Collection;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

// JobId (nullable) carries the durable job id so ArchiveLoadService can stamp a per-month
// SetTouched breadcrumb before each fetch; null for callers with no durable job row.
public sealed record ArchiveLoadRequest(CollectionAsset Asset, string FeedName, string Interval, DateOnly From, DateOnly To, string? JobId = null);

public interface IArchiveLoadService
{
    Task<bool> Run(ArchiveLoadRequest req, IJobProgressSink sink, CancellationToken ct = default);
}
