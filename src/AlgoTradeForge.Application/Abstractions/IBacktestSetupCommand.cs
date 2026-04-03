namespace AlgoTradeForge.Application.Abstractions;

public interface IBacktestSetupCommand
{
    IReadOnlyList<DataSubscriptionDto> DataSubscriptions { get; }
    BacktestSettingsDto BacktestSettings { get; }
    string StrategyName { get; }
    bool UseDetailedExecutionLogic { get; }
    IDictionary<string, object>? StrategyParameters { get; }
}
