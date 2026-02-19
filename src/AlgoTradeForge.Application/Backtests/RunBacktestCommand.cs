using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Backtests;

public sealed record RunBacktestCommand : ICommand<BacktestResultDto>
{
    public required string AssetName { get; init; }
    public required string Exchange { get; init; }
    public required string StrategyName { get; init; }
    public required decimal InitialCash { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public decimal CommissionPerTrade { get; init; } = 0m;
    public long SlippageTicks { get; init; }
    public TimeSpan? TimeFrame { get; init; }
    public bool UseDetailedExecutionLogic { get; init; }
    public IDictionary<string, object>? StrategyParameters { get; init; }
}
