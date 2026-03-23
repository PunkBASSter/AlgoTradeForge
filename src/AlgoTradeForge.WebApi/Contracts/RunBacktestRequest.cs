namespace AlgoTradeForge.WebApi.Contracts;

public sealed record DataSubscriptionInput
{
    public required string AssetName { get; init; }
    public required string Exchange { get; init; }
    public string? TimeFrame { get; init; }
}

public sealed record BacktestSettingsInput
{
    public required decimal InitialCash { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public decimal CommissionPerTrade { get; init; } = 0m;
    public long SlippageTicks { get; init; }
}

public sealed record RunBacktestRequest
{
    public required DataSubscriptionInput DataSubscription { get; init; }
    public required BacktestSettingsInput BacktestSettings { get; init; }
    public required string StrategyName { get; init; }
    public Dictionary<string, object>? StrategyParameters { get; init; }
}
