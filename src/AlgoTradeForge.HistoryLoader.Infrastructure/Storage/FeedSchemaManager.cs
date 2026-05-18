using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlgoTradeForge.Application.IO;
using AlgoTradeForge.Application.Threading;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// Per-<c>feeds.json</c> synchronized writer. One <see cref="SemaphoreSlim"/> per asset path
/// serializes read-merge-write so parallel writers on distinct feed-ids of the same asset don't
/// lose each other's entries. File I/O is routed through <see cref="IFileStorage"/>; the local
/// backend's <c>WriteAllText</c> is atomic (<c>*.tmp</c> + same-volume rename with
/// <c>flushToDisk:true</c>). Different asset directories use independent semaphores so
/// cross-asset writes proceed in parallel.
/// </summary>
internal sealed class FeedSchemaManager : ISchemaManager
{
    private readonly IFileStorage _fs;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public event Action<string>? ManifestChanged;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FeedSchemaManager(IFileStorage fs)
    {
        _fs = fs;
    }

    public async Task<FeedMetadata?> Load(string assetDir, CancellationToken ct = default)
    {
        var path = FeedsJsonPath(assetDir);
        var gate = GetLock(path);
        using var _ = await gate.LockAsync(ct);
        return await LoadUnsafe(path, ct);
    }

    public async Task EnsureSchema(
        string assetDir,
        string feedName,
        string interval,
        string[] columns,
        AutoApplySpec? autoApply = null,
        CancellationToken ct = default)
    {
        var path = FeedsJsonPath(assetDir);
        var gate = GetLock(path);
        using (var _ = await gate.LockAsync(ct))
        {
            // Re-read inside the lock so a concurrent earlier writer's entries are visible —
            // otherwise two parallel writers each merge against pre-write state and clobber
            // each other.
            var existing = await LoadUnsafe(path, ct) ?? new FeedMetadata();

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

            await AtomicWriteUnsafe(path, updated, ct);
        }

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    public async Task EnsureAltBarFeed(string assetDir, string feedId, AltBarFeedSpec spec, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedId);
        ArgumentNullException.ThrowIfNull(spec);

        var path = FeedsJsonPath(assetDir);
        var gate = GetLock(path);
        using (var _ = await gate.LockAsync(ct))
        {
            var existing = await LoadUnsafe(path, ct) ?? new FeedMetadata();

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

            await AtomicWriteUnsafe(path, updated, ct);
        }

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    public async Task EnsureAltBarWithSidecar(
        string assetDir,
        string parentFeedId,
        AltBarFeedSpec parentSpec,
        string sidecarFeedId,
        string[] sidecarColumns,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(parentFeedId);
        ArgumentException.ThrowIfNullOrEmpty(sidecarFeedId);
        ArgumentNullException.ThrowIfNull(parentSpec);
        ArgumentNullException.ThrowIfNull(sidecarColumns);

        var path = FeedsJsonPath(assetDir);
        var gate = GetLock(path);
        using (var _ = await gate.LockAsync(ct))
        {
            var existing = await LoadUnsafe(path, ct) ?? new FeedMetadata();

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

            await AtomicWriteUnsafe(path, updated, ct);
        }

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    public async Task<bool> SetAutoApplyParams(
        string assetDir,
        string feedName,
        double? cap,
        double? floor,
        int? intervalHours,
        bool? disclaimer,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedName);

        var path = FeedsJsonPath(assetDir);
        var gate = GetLock(path);
        using (var _ = await gate.LockAsync(ct))
        {
            var existing = await LoadUnsafe(path, ct);
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

            await AtomicWriteUnsafe(path, updated, ct);
        }

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
        return true;
    }

    public Task RemoveFeed(string assetDir, string feedId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedId);
        return RemoveFeedsInternal(assetDir, [feedId], ct);
    }

    public Task RemoveFeedAndSidecar(string assetDir, string feedId, string sidecarFeedId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedId);
        ArgumentException.ThrowIfNullOrEmpty(sidecarFeedId);
        return RemoveFeedsInternal(assetDir, [feedId, sidecarFeedId], ct);
    }

    private async Task RemoveFeedsInternal(string assetDir, string[] feedIds, CancellationToken ct)
    {
        var path = FeedsJsonPath(assetDir);
        var gate = GetLock(path);
        var raised = false;
        using (var _ = await gate.LockAsync(ct))
        {
            var existing = await LoadUnsafe(path, ct);
            if (existing is null) return;

            var updated = new Dictionary<string, FeedDefinition>(existing.Feeds);
            var removedAny = false;
            foreach (var id in feedIds)
            {
                if (updated.Remove(id)) removedAny = true;
            }
            if (!removedAny) return;

            await AtomicWriteUnsafe(path, new FeedMetadata
            {
                Feeds   = updated,
                Candles = existing.Candles,
            }, ct);
            raised = true;
        }

        if (raised) ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    public async Task EnsureCandleConfig(string assetDir, int decimalDigits, string interval, CancellationToken ct = default)
    {
        var path = FeedsJsonPath(assetDir);
        var gate = GetLock(path);
        using (var _ = await gate.LockAsync(ct))
        {
            var existing = await LoadUnsafe(path, ct) ?? new FeedMetadata();

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

            await AtomicWriteUnsafe(path, updated, ct);
        }

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    private SemaphoreSlim GetLock(string path) =>
        _locks.GetOrAdd(path, _ => new SemaphoreSlim(initialCount: 1, maxCount: 1));

    private static string FeedsJsonPath(string assetDir) =>
        Path.GetFullPath(Path.Combine(assetDir, "feeds.json"));

    private async Task<FeedMetadata?> LoadUnsafe(string path, CancellationToken ct)
    {
        if (!await _fs.Exists(path, ct))
            return null;

        var json = await _fs.ReadAllText(path, ct);

        // Parse as JsonNode first so the validator can distinguish "property absent" from
        // "property == null" — JsonSerializer collapses both into null.
        var node = JsonNode.Parse(json);
        FeedMetadataValidator.ValidateOrThrow(node);

        return JsonSerializer.Deserialize<FeedMetadata>(json, JsonOptions);
    }

    private async Task AtomicWriteUnsafe(string targetPath, FeedMetadata metadata, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(metadata, JsonOptions);

        // Re-validate before the on-disk write so a future refactor that drops a presence
        // guarantee (e.g. imbalanceReconstructionMethod) fails loudly here, not at read time.
        var node = JsonNode.Parse(json);
        FeedMetadataValidator.ValidateOrThrow(node);

        // IFileStorage.WriteAllText is atomic on local FS (.tmp + Move with flushToDisk:true)
        // and on S3 (single PutObject); parent dir creation is handled by OpenWriteSession.
        await _fs.WriteAllText(targetPath, json, ct: ct);
    }
}
