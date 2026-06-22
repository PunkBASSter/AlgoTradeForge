using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;

// Bridges the relay's best-effort trade tap to the data-plane tick router.
public sealed class TickRouterTradeTap(ITickRouter router) : IRelayTradeTap
{
    public void OnTrade(string instrument, in TradeTick tick) => router.Publish(instrument, in tick);
}
