using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IFeedStatusStore
{
    Task<FeedStatus?> Load(string assetDir, string feedName, string interval, CancellationToken ct = default);
    Task Save(string assetDir, string feedName, string interval, FeedStatus status, CancellationToken ct = default);
}
