using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Abstractions;

public interface IHistoryRepository
{
    Task<TimeSeries<Int64Bar>> Load(DataFeedSubscription subscription, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// TODO: can remove as Asset is already inside DataFeedSubscription
    /// </summary>
    Task<TimeSeries<Int64Bar>> Load(Asset asset, DataFeedSubscription subscription, DateOnly from, DateOnly to, CancellationToken ct = default);
}
