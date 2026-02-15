using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public sealed record BacktestOptions
{
    public required decimal InitialCash { get; init; }
    public required Asset Asset { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public decimal CommissionPerTrade { get; init; } = 0m;
    public decimal SlippageTicks { get; init; } = 0m;
    public bool UseDetailedExecutionLogic { get; init; }
}
