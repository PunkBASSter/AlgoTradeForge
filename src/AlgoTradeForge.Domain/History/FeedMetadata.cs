namespace AlgoTradeForge.Domain.History;

/// <summary>
/// Deserialized from <c>feeds.json</c> in the asset partition directory.
/// Describes all available feeds for an asset with schemas, intervals, and auto-apply config.
/// Adding new data types = add CSV files + update feeds.json. Zero engine code changes for informational feeds.
/// </summary>
public sealed class FeedMetadata
{
    public Dictionary<string, FeedDefinition> Feeds { get; init; } = [];
    public CandleConfig? Candles { get; init; }
}

public sealed class FeedDefinition
{
    public required string Interval { get; init; }
    public string[] Columns { get; init; } = [];
    public AutoApplyDefinition? AutoApply { get; init; }
}

public sealed class CandleConfig
{
    public decimal ScaleFactor { get; init; } = 100m;
    public string[] Intervals { get; init; } = [];
}

public sealed class AutoApplyDefinition
{
    public required string Type { get; init; }
    public required string RateColumn { get; init; }
    public string? SignConvention { get; init; }
}
