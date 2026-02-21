using AlgoTradeForge.Domain.Events;

namespace AlgoTradeForge.Application.Events;

public sealed record RunMeta
{
    // Identity
    public required string StrategyName { get; init; }
    public required string StrategyVersion { get; init; }
    public required string AssetName { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required long InitialCash { get; init; }
    public required ExportMode RunMode { get; init; }
    public required DateTimeOffset RunTimestamp { get; init; }
    public IDictionary<string, object>? StrategyParameters { get; init; }

    // Summary
    public required int TotalBarsProcessed { get; init; }
    public required long FinalEquity { get; init; }
    public required int TotalFills { get; init; }
    public required TimeSpan Duration { get; init; }
}
