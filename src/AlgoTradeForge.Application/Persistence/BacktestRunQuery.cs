namespace AlgoTradeForge.Application.Persistence;

public sealed record BacktestRunQuery
{
    public const int MaxLimit = 500;

    public string? StrategyName { get; init; }
    public string? AssetName { get; init; }
    public string? Exchange { get; init; }
    public string? TimeFrame { get; init; }
    public bool? StandaloneOnly { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    private readonly int _limit = 50;
    public int Limit { get => _limit; init => _limit = Math.Clamp(value, 1, MaxLimit); }

    private readonly int _offset;
    public int Offset { get => _offset; init => _offset = Math.Max(value, 0); }
}

public sealed record OptimizationRunQuery
{
    public const int MaxLimit = 500;

    public string? StrategyName { get; init; }
    public string? AssetName { get; init; }
    public string? Exchange { get; init; }
    public string? TimeFrame { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    private readonly int _limit = 50;
    public int Limit { get => _limit; init => _limit = Math.Clamp(value, 1, MaxLimit); }

    private readonly int _offset;
    public int Offset { get => _offset; init => _offset = Math.Max(value, 0); }
}
