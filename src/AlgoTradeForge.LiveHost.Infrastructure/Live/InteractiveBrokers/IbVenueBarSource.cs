using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// IB venue-published 5s bar lane (reqRealTimeBars "TRADES"). Mirrors KlineVenueBarSource: resolves
// the contract in Start(), subscribes, scales each bar via the asset ScaleContext, emits via onBar,
// and keeps a bounded Recent.
internal sealed class IbVenueBarSource(
    IIbMarketDataSession session, IIbContractResolver resolver, IbContract spec, ScaleContext scale,
    Action<Int64Bar, bool> onBar, int recentCapacity = 256) : IBarSource
{
    private readonly BoundedRecent<Int64Bar> _recent = new(recentCapacity);

    public IReadOnlyList<Int64Bar> Recent => _recent.Snapshot();

    public async Task Start()
    {
        // Ensure the shared socket is up before resolving/subscribing: when a collection has only TimeBar/AltBar
        // IB feeds (no Tick instruments), the relay pump never streams and so never connects, and even reqContractDetails
        // needs the socket. Connect is idempotent, so this is a no-op when the connector already connected.
        await session.Connect();
        var resolved = await resolver.Resolve(spec);
        session.SubscribeRealtimeBars(resolved, OnBar);
    }

    private void OnBar(IbRealtimeBar b)
    {
        var bar = new Int64Bar(
            b.DateSec * 1000,
            scale.FromMarketPrice((decimal)b.Open),
            scale.FromMarketPrice((decimal)b.High),
            scale.FromMarketPrice((decimal)b.Low),
            scale.FromMarketPrice((decimal)b.Close),
            MoneyConvert.ToLong(b.Volume));
        _recent.Add(bar);
        onBar(bar, false);
    }
}
