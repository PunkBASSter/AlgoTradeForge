using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IFeedStatusStore
{
    FeedStatus? Load(string assetDir, string feedName, string interval);
    void Save(string assetDir, string feedName, string interval, FeedStatus status);
}
