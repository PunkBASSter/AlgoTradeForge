using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ISchemaManager
{
    /// <summary>Loads <c>feeds.json</c> for the asset directory or returns <c>null</c> if absent.</summary>
    Task<FeedMetadata?> Load(string assetDir, CancellationToken ct = default);

    Task EnsureSchema(string assetDir, string feedName, string interval, string[] columns, AutoApplySpec? autoApply = null, CancellationToken ct = default);
    Task EnsureCandleConfig(string assetDir, int decimalDigits, string interval, CancellationToken ct = default);

    /// <summary>Writes (or replaces) the manifest entry for an alt-bar feed under an exclusive read-merge-write lock.</summary>
    Task EnsureAltBarFeed(string assetDir, string feedId, AltBarFeedSpec spec, CancellationToken ct = default);

    /// <summary>
    /// Atomic two-entry write for EqIV: parent alt-bar entry + its analytical <c>.flow</c> sidecar
    /// entry under a single exclusive lock so readers see either both-present or both-absent.
    /// The parent's <see cref="AltBarFeedSpec.Sidecar"/> is overridden to <paramref name="sidecarFeedId"/>.
    /// </summary>
    Task EnsureAltBarWithSidecar(
        string assetDir,
        string parentFeedId,
        AltBarFeedSpec parentSpec,
        string sidecarFeedId,
        string[] sidecarColumns,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces the bounded-rate params (cap/floor/intervalHours/disclaimer) on an existing
    /// feed's <see cref="AutoApplyDefinition"/>. Null args clear the value, they do not
    /// preserve it — callers must pass a complete venue snapshot. Returns <c>false</c> if the
    /// feed entry or its <c>AutoApply</c> sub-object is absent (no auto-apply mechanism to
    /// constrain, so caller should skip this asset rather than synthesize one).
    /// </summary>
    Task<bool> SetAutoApplyParams(
        string assetDir,
        string feedName,
        double? cap,
        double? floor,
        int? intervalHours,
        bool? disclaimer,
        CancellationToken ct = default);

    /// <summary>Removes one feed entry from <c>feeds.json</c>. No-op if the entry isn't present.</summary>
    Task RemoveFeed(string assetDir, string feedId, CancellationToken ct = default);

    /// <summary>Atomically removes a parent feed entry and its sidecar under one exclusive lock — both-present or both-absent.</summary>
    Task RemoveFeedAndSidecar(string assetDir, string feedId, string sidecarFeedId, CancellationToken ct = default);

    /// <summary>
    /// Raised after a successful manifest mutation; argument is the asset directory absolute path.
    /// Subscribers invalidate per-asset cache keys without polling.
    /// </summary>
    event Action<string> ManifestChanged;
}

/// <summary>All-fields spec for writing an alt-bar entry into <c>feeds.json</c>.</summary>
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
