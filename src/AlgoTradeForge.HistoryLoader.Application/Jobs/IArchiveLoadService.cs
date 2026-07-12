using AlgoTradeForge.HistoryLoader.Application.Collection;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public sealed record ArchiveLoadRequest(CollectionAsset Asset, string FeedName, string Interval, DateOnly From, DateOnly To);

public interface IArchiveLoadService
{
    Task<bool> Run(ArchiveLoadRequest req, IJobProgressSink sink, CancellationToken ct = default);
}
