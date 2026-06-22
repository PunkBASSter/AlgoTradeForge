using System.Threading.Channels;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

// Precomputed routing index for one registered session: its bar/tick interests
// (each paired with the resolved DataSubscription the strategy callback receives)
// plus the session's market-data writer. Bars route unconditionally (the strategy is
// IInt64BarStrategy); ticks route by ITradeTickStrategy capability.
internal sealed class SessionInterest(
    IInt64BarStrategy strategy,
    ITradeTickStrategy? tradeTickStrategy,
    ChannelWriter<Action> dataWriter,
    IReadOnlyList<BarInterest> barInterests,
    IReadOnlyList<TickInterest> tickInterests)
{
    public IInt64BarStrategy Strategy { get; } = strategy;
    public ITradeTickStrategy? TradeTickStrategy { get; } = tradeTickStrategy;
    public ChannelWriter<Action> DataWriter { get; } = dataWriter;
    public IReadOnlyList<BarInterest> BarInterests { get; } = barInterests;
    public IReadOnlyList<TickInterest> TickInterests { get; } = tickInterests;

    public static SessionInterest Build(LiveSessionRegistration r)
    {
        // RawSubscriptions pair positionally with resolved Subscriptions (both built from the
        // same source subscriptions in order; see StartLiveSession wiring, T15). The handler
        // guarantees equal length — a mismatch is a wiring regression that must fail loudly
        // rather than silently truncate (and thus misroute) under a Math.Min.
        if (r.RawSubscriptions.Count != r.Subscriptions.Count)
            throw new InvalidOperationException(
                $"Subscription pairing mismatch: {r.RawSubscriptions.Count} raw vs " +
                $"{r.Subscriptions.Count} resolved for session {r.SessionId}.");

        var bars = new List<BarInterest>();
        var ticks = new List<TickInterest>();

        var tradeTickStrategy = r.Strategy as ITradeTickStrategy;

        var count = r.RawSubscriptions.Count;
        for (var i = 0; i < count; i++)
        {
            var raw = r.RawSubscriptions[i];
            var resolved = r.Subscriptions[i];

            // INSTRUMENT KEY CONTRACT (T11/T15 must match): the instrument string is the
            // subscription's AssetName (== resolved DataSubscription.Asset.Name).
            var instrument = raw.AssetName;

            switch (raw)
            {
                case TimeBarSubscription tb:
                    bars.Add(new BarInterest(instrument, BarSpecKey.TimeBar(tb.TimeFrame), resolved));
                    break;
                case AltBarSubscription ab:
                    bars.Add(new BarInterest(instrument, BarSpecKey.AltBar(ab.FeedId), resolved));
                    break;
                case TickSubscription when tradeTickStrategy is not null:
                    ticks.Add(new TickInterest(instrument, resolved));
                    break;
            }
        }

        return new SessionInterest(r.Strategy, tradeTickStrategy, r.DataWriter, bars, ticks);
    }
}
