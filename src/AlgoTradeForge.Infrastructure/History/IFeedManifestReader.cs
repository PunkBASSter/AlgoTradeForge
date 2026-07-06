using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Reads the per-asset <c>feeds.json</c> manifest. Single source of truth for locating,
/// opening, and deserializing the manifest — callers (asset resolution, feed-context
/// building, timeframe resolution) share this instead of each rolling their own
/// Exists → OpenRead → Deserialize → catch block.
/// </summary>
public interface IFeedManifestReader
{
    /// <summary>
    /// Returns the deserialized manifest, or <c>null</c> when it is absent OR unreadable
    /// (corrupt JSON / I/O error — logged as a warning). Callers that require the manifest
    /// MUST treat <c>null</c> as an error; callers with a legitimate no-manifest fallback
    /// may proceed.
    /// </summary>
    Task<FeedMetadata?> Read(string dataRoot, string exchange, string assetDir, CancellationToken ct = default);
}
