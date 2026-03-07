using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Application.Live;

public sealed record LiveSessionSnapshot(
    IReadOnlyList<Int64Bar> Bars,
    IReadOnlyList<Fill> Fills,
    IReadOnlyList<Order> PendingOrders,
    IReadOnlyDictionary<string, Position> Positions,
    long Cash,
    long InitialCash,
    decimal ExchangeBalance,
    Asset PrimaryAsset,
    IReadOnlyList<DataSubscription> Subscriptions,
    IReadOnlyList<SubscriptionLastBar> LastBarsPerSubscription,
    IReadOnlyList<ExchangeTradeDto> ExchangeTrades);

public sealed record SubscriptionLastBar(DataSubscription Subscription, Int64Bar Bar);
