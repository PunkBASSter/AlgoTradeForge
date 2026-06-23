using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

// One (instrument, bar-spec) a session subscribes to, paired with the resolved
// DataFeedSubscription passed to the strategy's OnBarStart/OnBarComplete callback.
internal readonly record struct BarInterest(string Instrument, BarSpecKey Spec, DataFeedSubscription Subscription);
