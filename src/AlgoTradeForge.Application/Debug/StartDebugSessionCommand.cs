using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Application.Debug;

public sealed record StartDebugSessionCommand : ICommand<DebugSessionDto>, IBacktestSetupCommand
{
    public required IReadOnlyList<DataFeedSubscription> DataSubscriptions { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public required string StrategyName { get; init; }
    public bool UseDetailedExecutionLogic { get; init; }
    public IDictionary<string, object>? StrategyParameters { get; init; }
}
