using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public interface ITickRouter
{
    void Publish(string instrument, in TradeTick tick);

    // Wires (or ref-counts) the shared bar sources for a session's subscriptions.
    ValueTask EnsureSources(LiveSessionRegistration registration, Func<string, ScaleContext> scaleFor);

    // Read-only snapshot of a shared source's Recent ring; empty if no such source exists.
    IReadOnlyList<Int64Bar> RecentBars(string instrument, BarSpecKey spec);

    // Drops the session's references; disposes any source whose last sharer leaves.
    ValueTask RemoveSources(Guid sessionId);
}
