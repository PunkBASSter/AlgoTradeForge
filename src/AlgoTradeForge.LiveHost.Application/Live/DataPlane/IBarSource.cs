using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

public interface IBarSource
{
    IReadOnlyList<Int64Bar> Recent { get; }

    // Venue-published sources (kline WS) subscribe here; tick-aggregation sources need no start.
    Task Start() => Task.CompletedTask;
}
