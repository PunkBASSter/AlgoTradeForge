using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Application.Collection;

/// <summary>What this LiveHost captures: a set of root data-feed subscriptions.</summary>
public sealed record CollectionConfig(IReadOnlyList<DataFeedSubscription> Feeds);
