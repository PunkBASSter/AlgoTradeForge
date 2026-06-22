using System;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public interface IBarSourceResolver
{
    // Returns null for raw-tick subscriptions (no bar source). onBar's bool is isStart.
    IBarSource? Resolve(string instrument, DataFeedSubscription subscription, ScaleContext scale, Action<Int64Bar, bool> onBar);
}
