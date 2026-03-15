using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IFeedWriter
{
    void Write(string assetDir, string feedName, string interval, string[] columns, FeedRecord record);
    long? ResumeFrom(string assetDir, string feedName, string interval);
}
