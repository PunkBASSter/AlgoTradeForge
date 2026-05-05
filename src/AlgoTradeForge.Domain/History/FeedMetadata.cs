// JSON case convention: the on-disk `feeds.json` schema is camelCase via
// `JsonNamingPolicy.CamelCase` in `FeedSchemaManager`. Don't add `[JsonPropertyName]`
// attributes to "fix" individual properties — that creates a heterogeneous schema where
// some keys are camelCase and others snake_case.

using System.Text.Json.Serialization;

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
    /// <summary>
    /// Optional discriminator. Legacy time-bar feeds leave this null and rely on
    /// <see cref="Interval"/>. New feeds set
    /// <c>"OHLCV_TimeBar" | "OHLCV_AltBar" | "Tick" | "Side" | "aggregated"</c>.
    /// </summary>
    public string? Kind { get; init; }

    /// <summary>
    /// Native interval for time-bar / interval-bearing feeds (e.g. <c>"1h"</c>, <c>"8h"</c>).
    /// Variable-duration feeds (alt bars, ticks) leave this null.
    /// </summary>
    public string? Interval { get; init; }

    public string[] Columns { get; init; } = [];

    public AutoApplyDefinition? AutoApply { get; init; }

    public AggregatedTypeInfo? Type { get; init; }
    public AggregatedSourceInfo? Source { get; init; }
    public ThresholdInfo? Threshold { get; init; }
    public BuildInfo? Build { get; init; }
    public FidelityInfo? Fidelity { get; init; }

    public string? FirstBarTs { get; init; }
    public string? LastBarTs { get; init; }

    /// <summary>Sibling feed-id pointing to the analytical sidecar (e.g. <c>"EqI_ticks_500000.flow"</c>).</summary>
    public string? Sidecar { get; init; }

    /// <summary>
    /// When <c>true</c>, <c>CsvFeedSeriesLoader</c> parses empty cells as <c>NaN</c>
    /// instead of throwing.
    /// </summary>
    public bool? NullableColumns { get; init; }
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

public sealed class AggregatedTypeInfo
{
    public required string Code { get; init; }     // "EqV" | "EqT" | "EqD" | "EqI" | "Range" | "Renko"
    public string? Name { get; init; }              // "EqualVolume", "EqualImbalance", ...
}

public sealed class AggregatedSourceInfo
{
    public required string Feed { get; init; }     // "1m" | "5m" | ... | "ticks"
    public string? FirstTs { get; init; }
    public string? LastTs { get; init; }
    public long? RecordCount { get; init; }
}

public sealed class ThresholdInfo
{
    public required decimal Value { get; init; }   // absolute, canonical units
    public required string Unit { get; init; }     // "base_asset" | "quote_asset" | "trades"
    public required string InputMode { get; init; } // "absolute" | "convenience"
    public string? ConvenienceInput { get; init; }
}

public sealed class BuildInfo
{
    public string? ToolVersion { get; init; }
    public string? BuiltAt { get; init; }
    public double? DurationSeconds { get; init; }
    public long? BarCount { get; init; }
    public string[]? PartitionsWritten { get; init; }
    public int? MaxPartitionSizeMB { get; init; }

    /// <summary>
    /// Cumulative count of <c>+1 ms</c> bumps applied to enforce strict-monotonic
    /// <c>bar.ts_open</c> on tick-source aggregations. Always 0 (or absent) for time-bar
    /// source jobs. High values relative to source record count indicate clustered
    /// exchange activity (volatility windows).
    /// </summary>
    public long? MonotonicBumps { get; init; }

    /// <summary>
    /// Count of strictly out-of-order tick records the source decorator recovered from
    /// (raw ts &lt; prev). Distinct from <see cref="MonotonicBumps"/>: non-zero values here
    /// indicate a real upstream ordering defect (ingestor bug, pagination misorder), not
    /// benign equal-millisecond clustering. Always absent for time-bar source jobs.
    /// </summary>
    public long? MonotonicRegressions { get; init; }
}

public sealed class FidelityInfo
{
    public double? EstimatedOvershootPct { get; init; }
    public double? ActualOvershootPct { get; init; }
    public double? MaxOvershootPct { get; init; }
    public double? MedianSourceRecordValue { get; init; }
    public double? NFactor { get; init; }

    /// <summary>
    /// "tick_signed" | "m1_taker_buy_proxy" | <c>null</c> (non-EqI). The JSON property
    /// MUST be present even when null on non-EqI feeds — absence indicates a malformed
    /// manifest. Forced serialization via the <see cref="JsonIgnoreAttribute"/> override.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ImbalanceReconstructionMethod { get; init; }
}
