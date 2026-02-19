namespace AlgoTradeForge.WebApi.Contracts;

public sealed record RunBacktestRequest
{
    public required string AssetName { get; init; }
    public required string Exchange { get; init; }
    public required string StrategyName { get; init; }
    public required decimal InitialCash { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public decimal CommissionPerTrade { get; init; } = 0m;
    public long SlippageTicks { get; init; }
    public string? TimeFrame { get; init; }
    public Dictionary<string, object>? StrategyParameters { get; init; }
}
