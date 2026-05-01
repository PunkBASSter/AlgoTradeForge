using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// Self-contained input for one aggregation run (TRD §6.5). Constructed by the endpoint
/// once it has resolved the request payload (P0-5 wire schema → canonical units, eligibility,
/// scale context). Carries everything the pipeline needs without re-reading config or
/// re-parsing the request.
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

/// <summary>
/// Pipeline output (TRD §6.4). Snapshotted at finalize and lifted into the manifest by the
/// pipeline's <c>EnsureAltBarFeed</c> call before this is returned to the worker.
/// </summary>
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
    /// <summary>
    /// Phase 2b — companion <c>.flow</c> sidecar feed-id for EqI runs (TRD §5.4 SSE
    /// <c>complete</c> payload). <c>null</c> for non-EqI types.
    /// </summary>
    string? SidecarFeedId = null);
