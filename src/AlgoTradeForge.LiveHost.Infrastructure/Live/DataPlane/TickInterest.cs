using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

// One raw-tick instrument a session subscribes to, paired with the resolved
// DataSubscription passed to the strategy's OnTradeTick callback.
internal readonly record struct TickInterest(string Instrument, DataSubscription Subscription);
