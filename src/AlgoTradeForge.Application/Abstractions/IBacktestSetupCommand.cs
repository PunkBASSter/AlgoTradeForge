namespace AlgoTradeForge.Application.Abstractions;

public interface IBacktestSetupCommand
{
    string AssetName { get; }
    string Exchange { get; }
    string StrategyName { get; }
    decimal InitialCash { get; }
    DateTimeOffset StartTime { get; }
    DateTimeOffset EndTime { get; }
    decimal CommissionPerTrade { get; }
    long SlippageTicks { get; }
    TimeSpan? TimeFrame { get; }
    bool UseDetailedExecutionLogic { get; }
    IDictionary<string, object>? StrategyParameters { get; }
}
