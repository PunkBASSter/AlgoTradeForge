using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ISchemaManager
{
    /// <summary>
    /// Loads <c>feeds.json</c> for the asset directory or returns <c>null</c> if absent.
    /// Reads under a shared lock — concurrent readers run in parallel.
    /// </summary>
    FeedMetadata? Load(string assetDir);

    void EnsureSchema(string assetDir, string feedName, string interval, string[] columns, AutoApplySpec? autoApply = null);
    void EnsureCandleConfig(string assetDir, int decimalDigits, string interval);

    /// <summary>
    /// Writes (or replaces) the manifest entry for an alt-bar feed. Single-entry, atomic
    /// read-merge-write under exclusive lock — same atomicity contract as
    /// <see cref="EnsureSchema"/>. Used by <c>AggregationPipeline</c> to finalize a job.
    /// </summary>
    void EnsureAltBarFeed(string assetDir, string feedId, AltBarFeedSpec spec);

    /// <summary>
    /// Removes one feed entry from <c>feeds.json</c>. No-op if the entry isn't present.
    /// </summary>
    void RemoveFeed(string assetDir, string feedId);

    /// <summary>
    /// Atomically removes both a parent feed entry and its sidecar entry under one exclusive
    /// lock — readers see either both-present or both-absent, never half. Used by the
    /// <c>DELETE /feeds/{feedId}</c> cascade (TRD §5.5 / P1b-40).
    /// </summary>
    void RemoveFeedAndSidecar(string assetDir, string feedId, string sidecarFeedId);

    /// <summary>
    /// Raised after a successful manifest mutation. The argument is the asset directory
    /// absolute path (parent of <c>feeds.json</c>). Subscribers (e.g. catalog/eligibility caches)
    /// invalidate per-asset cache keys without polling.
    /// </summary>
    event Action<string> ManifestChanged;
}

/// <summary>
/// All-fields spec for writing an alt-bar entry into <c>feeds.json</c>. Maps directly to the
/// nested-record shape on <see cref="FeedDefinition"/> (<see cref="AggregatedTypeInfo"/>,
/// <see cref="AggregatedSourceInfo"/>, <see cref="ThresholdInfo"/>, <see cref="BuildInfo"/>,
/// <see cref="FidelityInfo"/>) so the manager doesn't have to know which fields are required
/// vs optional — that's the caller's responsibility.
/// </summary>
public sealed record AltBarFeedSpec(
    string Kind,                       // "OHLCV_AltBar"
    string[] Columns,                  // typically ["ts","o","h","l","c","vol"]
    AggregatedTypeInfo Type,
    AggregatedSourceInfo Source,
    ThresholdInfo Threshold,
    BuildInfo Build,
    FidelityInfo Fidelity,
    string? FirstBarTs,
    string? LastBarTs,
    string? Sidecar);
