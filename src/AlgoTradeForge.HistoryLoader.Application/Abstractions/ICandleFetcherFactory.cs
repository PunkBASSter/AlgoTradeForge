namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ICandleFetcherFactory
{
    ICandleFetcher Create(string exchangeKey);
}
