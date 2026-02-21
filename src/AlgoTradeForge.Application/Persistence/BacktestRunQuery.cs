namespace AlgoTradeForge.Application.Persistence;

public sealed record BacktestRunQuery
{
    public string? StrategyName { get; init; }
    public string? AssetName { get; init; }
    public string? Exchange { get; init; }
    public string? TimeFrame { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}

public sealed record OptimizationRunQuery
{
    public string? StrategyName { get; init; }
    public string? AssetName { get; init; }
    public string? Exchange { get; init; }
    public string? TimeFrame { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int Limit { get; init; } = 50;
    public int Offset { get; init; }
}
