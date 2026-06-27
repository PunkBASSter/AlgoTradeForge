using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;

namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IMarketDataSource
{
    void Register(LiveSessionRegistration registration);
    ValueTask EnsureSources(LiveSessionRegistration reg, Func<string, ScaleContext> scaleFor);
    IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec);
    ValueTask RemoveSources(Guid sessionId);
}
