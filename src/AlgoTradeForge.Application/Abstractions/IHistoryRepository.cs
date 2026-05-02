using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Abstractions;

public interface IHistoryRepository
{
    TimeSeries<Int64Bar> Load(DataSubscription subscription, DateOnly from, DateOnly to);

    /// <summary>
    /// Phase 4 (TRD §9.3) polymorphic loader for the new <see cref="DataFeedSubscription"/>
    /// surface. Dispatches by subtype to a kind-specific <c>DataFeedDescriptor</c> and
    /// delegates to <c>IInt64BarLoader</c>. <see cref="SideFeedSubscription"/> is rejected
    /// here — side feeds are <c>FeedSeries</c>, not <c>TimeSeries&lt;Int64Bar&gt;</c>; they
    /// flow through <c>IFeedContextBuilder</c>.
    /// </summary>
    TimeSeries<Int64Bar> Load(Asset asset, DataFeedSubscription subscription, DateOnly from, DateOnly to);
}
