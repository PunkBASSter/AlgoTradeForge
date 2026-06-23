using System;
using System.Collections.Generic;
using System.Threading.Channels;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public sealed record LiveSessionRegistration(
    Guid SessionId,
    IInt64BarStrategy Strategy,
    IReadOnlyList<DataFeedSubscription> Subscriptions,        // resolved (Asset + TimeFrame) per existing model TODO: can one sub property be removed?
    IReadOnlyList<DataFeedSubscription> RawSubscriptions, // typed kinds (TimeBar/AltBar/Tick) for routing
    ChannelWriter<Action> DataWriter);                    // session's market-data channel (drop-newest)
