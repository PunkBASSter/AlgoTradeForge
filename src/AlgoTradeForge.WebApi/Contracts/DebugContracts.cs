namespace AlgoTradeForge.WebApi.Contracts;

public sealed record StartDebugSessionRequest
{
    public required DataSubscriptionInput DataSubscription { get; init; }
    public required BacktestSettingsInput BacktestSettings { get; init; }
    public required string StrategyName { get; init; }
    public Dictionary<string, object>? StrategyParameters { get; init; }
}

public sealed record DebugCommandRequest
{
    public required string Command { get; init; }
    public long? SequenceNumber { get; init; }
    public long? TimestampMs { get; init; }
}
