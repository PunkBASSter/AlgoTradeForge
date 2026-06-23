using AlgoTradeForge.Domain.Strategy.Subscriptions;
namespace AlgoTradeForge.Domain.Strategy.Subscriptions;

public static class SubscriptionResolver
{
    public static DataFeedSubscription Resolve(DataFeedSubscription spec, Asset asset) =>
        spec with { Asset = asset };
}
