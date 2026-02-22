namespace AlgoTradeForge.Application.Progress;

public sealed record RunTimeoutOptions
{
    public TimeSpan BacktestTimeout { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan OptimizationTimeout { get; init; } = TimeSpan.FromHours(12);
}
