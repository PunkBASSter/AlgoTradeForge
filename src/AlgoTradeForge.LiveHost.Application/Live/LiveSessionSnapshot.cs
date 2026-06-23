using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.LiveHost.Application.Live;

public sealed record LiveSessionSnapshot(
    IReadOnlyList<Int64Bar> Bars,
    IReadOnlyList<Fill> Fills,
    IReadOnlyList<Order> PendingOrders,
    IReadOnlyDictionary<string, Position> Positions,
    long Cash,
    long InitialCash,
    decimal ExchangeBalance,
    Asset ExecutionAsset,
    IReadOnlyList<DataFeedSubscription> Subscriptions,
    IReadOnlyList<SubscriptionLastBar> LastBarsPerSubscription,
    IReadOnlyList<ExchangeTradeDto> ExchangeTrades);

// The most recent bar per bar-subscription, surfaced in the session snapshot for the UI's per-feed display. TODO: See N last bars, the last close should be mutable with incoming ticks
public sealed record SubscriptionLastBar(DataFeedSubscription Subscription, Int64Bar Bar);
