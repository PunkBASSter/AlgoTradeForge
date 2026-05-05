using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Self-contained input for one aggregation run. Built by the endpoint after resolving the
/// request payload to canonical units; the pipeline never re-reads config.
/// </summary>
public sealed record AggregationJob(
    string JobId,
    DataFeedDescriptor Source,
    string AssetDir,
    string OutcomeFeedId,             // canonical alt-bar id, e.g. "EqV_1m_1000"
    string TypeCode,
    decimal ThresholdAbsolute,        // canonical absolute units, for manifest
    long ThresholdScaled,             // tick/quant-scaled long, for accumulator
    string ThresholdUnit,             // "base_asset" | "quote_asset" | "trades"
    string ThresholdInputMode,        // "absolute" | "convenience"
    string? ThresholdConvenienceInput,
    ScaleContext SourceScale,
    ScaleContext AccumulatorScale,
    int MaxPartitionSizeMB,
    string ToolVersion);

/// <summary>Pipeline output snapshotted at finalize.</summary>
public sealed record AggregationResult(
    string JobId,
    string OutcomeFeedId,
    long BarCount,
    IReadOnlyList<string> PartitionsWritten,
    string? FirstBarTs,
    string? LastBarTs,
    double ActualOvershootPct,
    double MaxOvershootPct,
    double EstimatedOvershootPct,
    double MedianSourceRecordValue,
    double NFactor,
    double DurationSeconds,
    /// <summary>Companion <c>.flow</c> sidecar feed-id for EqIV runs; <c>null</c> for non-EqIV types.</summary>
    string? SidecarFeedId = null);
