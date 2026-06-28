using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IMarketDataSource //TODO: consider extracting generic version instead of Int64Bar type, e.g. for Tick or OrderBook data (or add feeds)
{
    void Register(LiveSessionRegistration registration);
    ValueTask EnsureSources(LiveSessionRegistration reg, Func<string, ScaleContext> scaleFor);
    IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec);
    ValueTask RemoveSources(Guid sessionId);
}
