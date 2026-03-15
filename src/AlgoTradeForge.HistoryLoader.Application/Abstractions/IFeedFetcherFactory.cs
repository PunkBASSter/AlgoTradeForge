namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface IFeedFetcherFactory
{
    IFeedFetcher Create(string exchangeKey, string feedName);
}
