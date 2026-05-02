using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Abstractions;

public interface IBacktestSetupCommand
{
    IReadOnlyList<DataFeedSubscription> DataSubscriptions { get; }
    BacktestSettingsDto BacktestSettings { get; }
    string StrategyName { get; }
    bool UseDetailedExecutionLogic { get; }
    IDictionary<string, object>? StrategyParameters { get; }
}
