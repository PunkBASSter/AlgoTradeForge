namespace AlgoTradeForge.WebApi.Contracts;

public sealed record StartDebugSessionRequest
{
    public required string AssetName { get; init; }
    public required string Exchange { get; init; }
    public required string StrategyName { get; init; }
    public required decimal InitialCash { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public decimal CommissionPerTrade { get; init; }
    public long SlippageTicks { get; init; }
    public string? TimeFrame { get; init; }
    public Dictionary<string, object>? StrategyParameters { get; init; }
}

public sealed record DebugCommandRequest
{
    public required string Command { get; init; }
    public long? SequenceNumber { get; init; }
    public long? TimestampMs { get; init; }
}
