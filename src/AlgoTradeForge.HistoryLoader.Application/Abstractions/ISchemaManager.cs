using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Abstractions;

public interface ISchemaManager
{
    /// <summary>Loads <c>feeds.json</c> for the asset directory or returns <c>null</c> if absent. Reads under a shared lock.</summary>
    FeedMetadata? Load(string assetDir);

    void EnsureSchema(string assetDir, string feedName, string interval, string[] columns, AutoApplySpec? autoApply = null);
    void EnsureCandleConfig(string assetDir, int decimalDigits, string interval);

    /// <summary>Writes (or replaces) the manifest entry for an alt-bar feed under an exclusive read-merge-write lock.</summary>
    void EnsureAltBarFeed(string assetDir, string feedId, AltBarFeedSpec spec);

    /// <summary>
    /// Atomic two-entry write for EqI: parent alt-bar entry + its analytical <c>.flow</c> sidecar
    /// entry under a single exclusive lock so readers see either both-present or both-absent.
    /// The parent's <see cref="AltBarFeedSpec.Sidecar"/> is overridden to <paramref name="sidecarFeedId"/>.
    /// </summary>
    void EnsureAltBarWithSidecar(
        string assetDir,
        string parentFeedId,
        AltBarFeedSpec parentSpec,
        string sidecarFeedId,
        string[] sidecarColumns);

    /// <summary>Removes one feed entry from <c>feeds.json</c>. No-op if the entry isn't present.</summary>
    void RemoveFeed(string assetDir, string feedId);

    /// <summary>Atomically removes a parent feed entry and its sidecar under one exclusive lock — both-present or both-absent.</summary>
    void RemoveFeedAndSidecar(string assetDir, string feedId, string sidecarFeedId);

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
