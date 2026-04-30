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
    /// Raised after a successful manifest mutation. The argument is the asset directory
    /// absolute path (parent of <c>feeds.json</c>). Subscribers (e.g. catalog/eligibility caches)
    /// invalidate per-asset cache keys without polling.
    /// </summary>
    event Action<string> ManifestChanged;
}
