namespace AlgoTradeForge.WebApi.Contracts;

public sealed record RunBacktestRequest
{
    public required string AssetName { get; init; }
    public required string StrategyName { get; init; }
    public required string BarSourceName { get; init; }
    public required decimal InitialCash { get; init; }
    public decimal CommissionPerTrade { get; init; } = 0m;
    public decimal SlippageTicks { get; init; } = 0m;
    public Dictionary<string, object>? StrategyParameters { get; init; }
}
