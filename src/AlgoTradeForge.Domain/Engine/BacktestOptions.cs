using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.Domain.Engine;

public sealed record BacktestOptions
{
    public required long InitialCash { get; init; }
    public required Asset Asset { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public long CommissionPerTrade { get; init; } = 0L;
    public long SlippageTicks { get; init; }
    public bool UseDetailedExecutionLogic { get; init; }
}
