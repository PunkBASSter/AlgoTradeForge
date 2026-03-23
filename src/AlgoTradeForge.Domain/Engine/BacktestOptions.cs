namespace AlgoTradeForge.Domain.Engine;

public sealed record BacktestOptions
{
    public required long InitialCash { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public decimal CommissionPerTrade { get; init; }
    public long SlippageTicks { get; init; }
    public bool UseDetailedExecutionLogic { get; init; }
}
