using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.LiveHost.Application.Live.Recovery;

/// <summary>
/// Everything a tick-aggregation bar source needs to catch up to the present at session start:
/// the warmup-bar loader (completed bars read cheaply from the persisted alt-bar feed), the replay
/// coordinator + request (tail source-record replay), and how many warmup bars to seed.
/// </summary>
public sealed record CatchupPlan(
    CatchupCoordinator Coordinator,
    ReplayRequest Request,
    IInt64BarLoader WarmupLoader,
    DataFeedDescriptor AltBarFeed,
    int WarmupBarCount,
    Action<Discontinuity>? OnDiscontinuity = null);
