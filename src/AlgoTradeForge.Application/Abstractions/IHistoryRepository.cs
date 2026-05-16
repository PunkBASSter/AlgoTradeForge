using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Abstractions;

public interface IHistoryRepository
{
    Task<TimeSeries<Int64Bar>> Load(DataSubscription subscription, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// Polymorphic loader for <see cref="DataFeedSubscription"/>. Rejects
    /// <c>SideFeedSubscription</c> — side feeds flow through <see cref="IFeedContextBuilder"/>.
    /// </summary>
    Task<TimeSeries<Int64Bar>> Load(Asset asset, DataFeedSubscription subscription, DateOnly from, DateOnly to, CancellationToken ct = default);
}
