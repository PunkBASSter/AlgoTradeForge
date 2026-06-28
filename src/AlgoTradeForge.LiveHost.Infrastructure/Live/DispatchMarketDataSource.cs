using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live;

// Thin transport-scoped data-plane seam: names the already-shared dispatch + tick router so
// multiple account targets can share one source. Venue-neutral; used by both Binance and IB connectors.
public sealed class DispatchMarketDataSource(IStrategyDispatch dispatch, ITickRouter tickRouter) : IMarketDataSource
{
    public void Register(LiveSessionRegistration registration) => dispatch.Register(registration);

    public ValueTask EnsureSources(LiveSessionRegistration reg, Func<string, ScaleContext> scaleFor) =>
        tickRouter.EnsureSources(reg, scaleFor);

    public IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec) =>
        tickRouter.RecentBars(instrument, spec);

    public ValueTask RemoveSources(Guid sessionId) => tickRouter.RemoveSources(sessionId);
}
