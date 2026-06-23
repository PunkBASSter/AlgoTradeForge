using System.Threading.Channels;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

// Precomputed routing index for one registered session: its bar/tick interests
// (each paired with the resolved DataFeedSubscription the strategy callback receives)
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
        var bars = new List<BarInterest>();
        var ticks = new List<TickInterest>();

        var tradeTickStrategy = r.Strategy as ITradeTickStrategy;

        foreach (var sub in r.Subscriptions)
        {
            // INSTRUMENT KEY CONTRACT (T11/T15 must match): the instrument string is the
            // subscription's AssetName (== resolved DataFeedSubscription.Asset.Name).
            var instrument = sub.AssetName;

            switch (sub)
            {
                case TimeBarSubscription tb:
                    bars.Add(new BarInterest(instrument, BarSpecKey.TimeBar(tb.TimeFrame), sub));
                    break;
                case AltBarSubscription ab:
                    bars.Add(new BarInterest(instrument, BarSpecKey.AltBar(ab.FeedId), sub));
                    break;
                case TickSubscription when tradeTickStrategy is not null:
                    ticks.Add(new TickInterest(instrument, sub));
                    break;
            }
        }

        return new SessionInterest(r.Strategy, tradeTickStrategy, r.DataWriter, bars, ticks);
    }
}
