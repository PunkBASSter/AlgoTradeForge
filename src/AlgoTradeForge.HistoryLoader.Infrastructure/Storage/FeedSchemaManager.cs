using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// <para>
/// Per-<c>(exchange, asset)</c> synchronized writer for <c>feeds.json</c>:
/// <list type="bullet">
///   <item>Shared lock for <see cref="Load"/>; multiple readers run concurrently.</item>
///   <item>Exclusive lock for <see cref="EnsureSchema"/> / <see cref="EnsureCandleConfig"/>;
///         the read-merge-write happens entirely inside the lock so parallel writers on
///         distinct feed-ids of the same asset don't lose each other's entries.</item>
///   <item><c>*.tmp</c> + atomic rename on every write — same-volume by construction
///         since the temp file lives in the same directory as the target.</item>
/// </list>
/// </para>
/// <para>
/// Different <c>(exchange, asset)</c> directories use independent locks, so cross-asset
/// writes proceed in parallel.
/// </para>
/// </summary>
internal sealed class FeedSchemaManager : ISchemaManager
{
    /// <summary>
    /// Per-<c>feeds.json</c>-path locks. Keyed by absolute path so two writers targeting
    /// the same asset directory share a lock; different assets use independent locks.
    /// </summary>
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _locks = new();

    /// <summary>
    /// Raised after a successful manifest mutation. The argument is the asset directory
    /// absolute path (parent of <c>feeds.json</c>). Subscribers (Phase 1b catalog/eligibility
    /// caches) use this to invalidate per-asset cache keys without polling.
    /// </summary>
    public event Action<string>? ManifestChanged;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FeedMetadata? Load(string assetDir)
    {
        var path = FeedsJsonPath(assetDir);
        var rwl = GetLock(path);
        rwl.EnterReadLock();
        try
        {
            return LoadUnsafe(path);
        }
        finally
        {
            rwl.ExitReadLock();
        }
    }

    public void EnsureSchema(
        string assetDir,
        string feedName,
        string interval,
        string[] columns,
        AutoApplySpec? autoApply = null)
    {
        var path = FeedsJsonPath(assetDir);
        var rwl = GetLock(path);
        rwl.EnterWriteLock();
        try
        {
            // Read-merge-write under exclusive lock: re-read so concurrent earlier writers
            // are visible. Without this re-read, two parallel writers could each load the
            // pre-write state, merge their own entry, and overwrite each other.
            var existing = LoadUnsafe(path) ?? new FeedMetadata();

            AutoApplyDefinition? autoApplyDef = autoApply is not null
                ? new AutoApplyDefinition
                {
                    Type = autoApply.Type,
                    RateColumn = autoApply.RateColumn,
                    SignConvention = autoApply.SignConvention,
                }
                : null;

            var updatedFeeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
            {
                [feedName] = new FeedDefinition
                {
                    Interval  = interval,
                    Columns   = columns,
                    AutoApply = autoApplyDef,
                }
            };

            var updated = new FeedMetadata
            {
                Feeds   = updatedFeeds,
                Candles = existing.Candles,
            };

            AtomicWriteUnsafe(assetDir, path, updated);
        }
        finally
        {
            rwl.ExitWriteLock();
        }

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    public void EnsureAltBarFeed(string assetDir, string feedId, AltBarFeedSpec spec)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedId);
        ArgumentNullException.ThrowIfNull(spec);

        var path = FeedsJsonPath(assetDir);
        var rwl = GetLock(path);
        rwl.EnterWriteLock();
        try
        {
            var existing = LoadUnsafe(path) ?? new FeedMetadata();

            var updatedFeeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
            {
                [feedId] = new FeedDefinition
                {
                    Kind            = spec.Kind,
                    Columns         = spec.Columns,
                    Type            = spec.Type,
                    Source          = spec.Source,
                    Threshold       = spec.Threshold,
                    Build           = spec.Build,
                    Fidelity        = spec.Fidelity,
                    FirstBarTs      = spec.FirstBarTs,
                    LastBarTs       = spec.LastBarTs,
                    Sidecar         = spec.Sidecar,
                }
            };

            var updated = new FeedMetadata
            {
                Feeds   = updatedFeeds,
                Candles = existing.Candles,
            };

            AtomicWriteUnsafe(assetDir, path, updated);
        }
        finally
        {
            rwl.ExitWriteLock();
        }

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    public void RemoveFeed(string assetDir, string feedId)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedId);
        RemoveFeedsInternal(assetDir, [feedId]);
    }

    public void RemoveFeedAndSidecar(string assetDir, string feedId, string sidecarFeedId)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedId);
        ArgumentException.ThrowIfNullOrEmpty(sidecarFeedId);
        RemoveFeedsInternal(assetDir, [feedId, sidecarFeedId]);
    }

    private void RemoveFeedsInternal(string assetDir, string[] feedIds)
    {
        var path = FeedsJsonPath(assetDir);
        var rwl = GetLock(path);
        var raised = false;
        rwl.EnterWriteLock();
        try
        {
            var existing = LoadUnsafe(path);
            if (existing is null) return;

            var updated = new Dictionary<string, FeedDefinition>(existing.Feeds);
            var removedAny = false;
            foreach (var id in feedIds)
            {
                if (updated.Remove(id)) removedAny = true;
            }
            if (!removedAny) return;

            AtomicWriteUnsafe(assetDir, path, new FeedMetadata
            {
                Feeds   = updated,
                Candles = existing.Candles,
            });
            raised = true;
        }
        finally
        {
            rwl.ExitWriteLock();
        }

        if (raised) ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    public void EnsureCandleConfig(string assetDir, int decimalDigits, string interval)
    {
        var path = FeedsJsonPath(assetDir);
        var rwl = GetLock(path);
        rwl.EnterWriteLock();
        try
        {
            var existing = LoadUnsafe(path) ?? new FeedMetadata();

            var scaleFactor = (decimal)Math.Pow(10, decimalDigits);

            var existingIntervals = existing.Candles?.Intervals ?? [];
            var updatedIntervals  = existingIntervals.Contains(interval)
                ? existingIntervals
                : [..existingIntervals, interval];

            var updated = new FeedMetadata
            {
                Feeds   = existing.Feeds,
                Candles = new CandleConfig
                {
                    ScaleFactor = scaleFactor,
                    Intervals   = updatedIntervals,
                },
            };

            AtomicWriteUnsafe(assetDir, path, updated);
        }
        finally
        {
            rwl.ExitWriteLock();
        }

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    // -------------------------------------------------------------------------

    private ReaderWriterLockSlim GetLock(string path) =>
        _locks.GetOrAdd(path, _ => new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion));

    private static string FeedsJsonPath(string assetDir) =>
        Path.GetFullPath(Path.Combine(assetDir, "feeds.json"));

    private static FeedMetadata? LoadUnsafe(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);

        // Parse first as JsonNode so we can run JSON-aware validation that distinguishes
        // "property absent" from "property == null". JsonSerializer collapses both into null.
        var node = JsonNode.Parse(json);
        FeedMetadataValidator.ValidateOrThrow(node);

        return JsonSerializer.Deserialize<FeedMetadata>(json, JsonOptions);
    }

    private static void AtomicWriteUnsafe(string assetDir, string targetPath, FeedMetadata metadata)
    {
        Directory.CreateDirectory(assetDir);

        var tmpPath = targetPath + ".tmp";

        var json = JsonSerializer.Serialize(metadata, JsonOptions);

        // Defensive: parse back and run the same validator the read path applies. Catches
        // schema-evolution bugs where a future refactor drops the
        // imbalanceReconstructionMethod presence guarantee on the write side.
        var node = JsonNode.Parse(json);
        FeedMetadataValidator.ValidateOrThrow(node);

        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, targetPath, overwrite: true);
    }
}
