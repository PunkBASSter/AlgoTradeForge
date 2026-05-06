using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// Per-<c>feeds.json</c> synchronized writer. Shared lock for <see cref="Load"/>; exclusive
/// read-merge-write under one lock so parallel writers on distinct feed-ids of the same asset
/// don't lose each other's entries. Each write is <c>*.tmp</c> + same-volume rename. Different
/// asset directories use independent locks so cross-asset writes proceed in parallel.
/// </summary>
internal sealed class FeedSchemaManager : ISchemaManager
{
    private readonly ConcurrentDictionary<string, ReaderWriterLockSlim> _locks = new();

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
            // Re-read inside the lock so a concurrent earlier writer's entries are visible —
            // otherwise two parallel writers each merge against pre-write state and clobber
            // each other.
            var existing = LoadUnsafe(path) ?? new FeedMetadata();

            AutoApplyDefinition? autoApplyDef = autoApply is not null
                ? new AutoApplyDefinition
                {
                    Type = autoApply.Type,
                    RateColumn = autoApply.RateColumn,
                    SignConvention = autoApply.SignConvention,
                    Cap = autoApply.Cap,
                    Floor = autoApply.Floor,
                    IntervalHours = autoApply.IntervalHours,
                    Disclaimer = autoApply.Disclaimer,
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

    public void EnsureAltBarWithSidecar(
        string assetDir,
        string parentFeedId,
        AltBarFeedSpec parentSpec,
        string sidecarFeedId,
        string[] sidecarColumns)
    {
        ArgumentException.ThrowIfNullOrEmpty(parentFeedId);
        ArgumentException.ThrowIfNullOrEmpty(sidecarFeedId);
        ArgumentNullException.ThrowIfNull(parentSpec);
        ArgumentNullException.ThrowIfNull(sidecarColumns);

        var path = FeedsJsonPath(assetDir);
        var rwl = GetLock(path);
        rwl.EnterWriteLock();
        try
        {
            var existing = LoadUnsafe(path) ?? new FeedMetadata();

            // Override the parent's Sidecar field; we own the linkage so the contract
            // "Sidecar references a live sidecar entry in this same write" stays exact.
            var parentEntry = new FeedDefinition
            {
                Kind            = parentSpec.Kind,
                Columns         = parentSpec.Columns,
                Type            = parentSpec.Type,
                Source          = parentSpec.Source,
                Threshold       = parentSpec.Threshold,
                Build           = parentSpec.Build,
                Fidelity        = parentSpec.Fidelity,
                FirstBarTs      = parentSpec.FirstBarTs,
                LastBarTs       = parentSpec.LastBarTs,
                Sidecar         = sidecarFeedId,
            };

            var sidecarEntry = new FeedDefinition
            {
                Kind            = "Side",
                Columns         = sidecarColumns,
                NullableColumns = true,
            };

            var updatedFeeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
            {
                [parentFeedId]  = parentEntry,
                [sidecarFeedId] = sidecarEntry,
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

    public bool SetAutoApplyParams(
        string assetDir,
        string feedName,
        double? cap,
        double? floor,
        int? intervalHours,
        bool? disclaimer)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedName);

        var path = FeedsJsonPath(assetDir);
        var rwl = GetLock(path);
        rwl.EnterWriteLock();
        try
        {
            var existing = LoadUnsafe(path);
            if (existing is null || !existing.Feeds.TryGetValue(feedName, out var feed))
                return false;

            if (feed.AutoApply is null)
                return false;

            var updatedAutoApply = new AutoApplyDefinition
            {
                Type = feed.AutoApply.Type,
                RateColumn = feed.AutoApply.RateColumn,
                SignConvention = feed.AutoApply.SignConvention,
                Cap = cap,
                Floor = floor,
                IntervalHours = intervalHours,
                Disclaimer = disclaimer,
            };

            var updatedFeed = new FeedDefinition
            {
                Kind = feed.Kind,
                Interval = feed.Interval,
                Columns = feed.Columns,
                AutoApply = updatedAutoApply,
                Type = feed.Type,
                Source = feed.Source,
                Threshold = feed.Threshold,
                Build = feed.Build,
                Fidelity = feed.Fidelity,
                FirstBarTs = feed.FirstBarTs,
                LastBarTs = feed.LastBarTs,
                Sidecar = feed.Sidecar,
                NullableColumns = feed.NullableColumns,
            };

            var updatedFeeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
            {
                [feedName] = updatedFeed
            };

            var updated = new FeedMetadata
            {
                Feeds = updatedFeeds,
                Candles = existing.Candles,
            };

            AtomicWriteUnsafe(assetDir, path, updated);
        }
        finally
        {
            rwl.ExitWriteLock();
        }

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
        return true;
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

    private ReaderWriterLockSlim GetLock(string path) =>
        _locks.GetOrAdd(path, _ => new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion));

    private static string FeedsJsonPath(string assetDir) =>
        Path.GetFullPath(Path.Combine(assetDir, "feeds.json"));

    private static FeedMetadata? LoadUnsafe(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);

        // Parse as JsonNode first so the validator can distinguish "property absent" from
        // "property == null" — JsonSerializer collapses both into null.
        var node = JsonNode.Parse(json);
        FeedMetadataValidator.ValidateOrThrow(node);

        return JsonSerializer.Deserialize<FeedMetadata>(json, JsonOptions);
    }

    private static void AtomicWriteUnsafe(string assetDir, string targetPath, FeedMetadata metadata)
    {
        Directory.CreateDirectory(assetDir);

        var tmpPath = targetPath + ".tmp";

        var json = JsonSerializer.Serialize(metadata, JsonOptions);

        // Re-validate before the on-disk write so a future refactor that drops a presence
        // guarantee (e.g. imbalanceReconstructionMethod) fails loudly here, not at read time.
        var node = JsonNode.Parse(json);
        FeedMetadataValidator.ValidateOrThrow(node);

        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, targetPath, overwrite: true);
    }
}
