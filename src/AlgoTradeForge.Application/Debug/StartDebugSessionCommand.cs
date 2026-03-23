using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Debug;

public sealed record StartDebugSessionCommand : ICommand<DebugSessionDto>, IBacktestSetupCommand
{
    public required DataSubscriptionDto DataSubscription { get; init; }
    public required BacktestSettingsDto BacktestSettings { get; init; }
    public required string StrategyName { get; init; }
    public bool UseDetailedExecutionLogic { get; init; }
    public IDictionary<string, object>? StrategyParameters { get; init; }
}
