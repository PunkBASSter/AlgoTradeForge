using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// Optimistic-concurrency writer for per-asset <c>feeds.json</c> manifests. Each write
/// loads the current ETag via <see cref="IFileStorage.ReadWithEtag"/>, applies a mutator
/// to the deserialized <see cref="FeedMetadata"/>, and persists via
/// <see cref="IFileStorage.WriteIfMatch"/>. On <see cref="ConcurrencyConflictException"/>
/// the operation retries up to <see cref="MaxAttempts"/> times with jittered exponential
/// backoff. Reads (<see cref="Load"/>) are lock-free; the storage backend's atomicity
/// invariants (atomic rename on local FS, native <c>If-Match</c> on S3) provide read
/// consistency. <see cref="ManifestChanged"/> fires exactly once per successful write.
/// </summary>
internal sealed class FeedSchemaManager : ISchemaManager
{
    private readonly IFileStorage _fs;

    public event Action<string>? ManifestChanged;

    private const int MaxAttempts = 5;

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
        var result = await LoadWithEtag(FeedsJsonPath(assetDir), ct);
        return result.Metadata;
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
        AutoApplyDefinition? autoApplyDef = autoApply is null ? null : new AutoApplyDefinition
        {
            Type = autoApply.Type,
            RateColumn = autoApply.RateColumn,
            SignConvention = autoApply.SignConvention,
            Cap = autoApply.Cap,
            Floor = autoApply.Floor,
            IntervalHours = autoApply.IntervalHours,
            Disclaimer = autoApply.Disclaimer,
        };

        await UpdateWithRetry(path, existing => new FeedMetadata
        {
            Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
            {
                [feedName] = new FeedDefinition
                {
                    Interval = interval,
                    Columns = columns,
                    AutoApply = autoApplyDef,
                }
            },
            Candles = existing.Candles,
        }, ct);

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    public async Task EnsureAltBarFeed(string assetDir, string feedId, AltBarFeedSpec spec, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(feedId);
        ArgumentNullException.ThrowIfNull(spec);

        var path = FeedsJsonPath(assetDir);

        await UpdateWithRetry(path, existing => new FeedMetadata
        {
            Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
            {
                [feedId] = new FeedDefinition
                {
                    Kind = spec.Kind,
                    Columns = spec.Columns,
                    Type = spec.Type,
                    Source = spec.Source,
                    Threshold = spec.Threshold,
                    Build = spec.Build,
                    Fidelity = spec.Fidelity,
                    FirstBarTs = spec.FirstBarTs,
                    LastBarTs = spec.LastBarTs,
                    Sidecar = spec.Sidecar,
                }
            },
            Candles = existing.Candles,
        }, ct);

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

        await UpdateWithRetry(path, existing =>
        {
            var parentEntry = new FeedDefinition
            {
                Kind = parentSpec.Kind,
                Columns = parentSpec.Columns,
                Type = parentSpec.Type,
                Source = parentSpec.Source,
                Threshold = parentSpec.Threshold,
                Build = parentSpec.Build,
                Fidelity = parentSpec.Fidelity,
                FirstBarTs = parentSpec.FirstBarTs,
                LastBarTs = parentSpec.LastBarTs,
                Sidecar = sidecarFeedId,
            };

            var sidecarEntry = new FeedDefinition
            {
                Kind = "Side",
                Columns = sidecarColumns,
                NullableColumns = true,
            };

            return new FeedMetadata
            {
                Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds)
                {
                    [parentFeedId] = parentEntry,
                    [sidecarFeedId] = sidecarEntry,
                },
                Candles = existing.Candles,
            };
        }, ct);

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
        var written = await UpdateWithRetry(path, existing =>
        {
            if (!existing.Feeds.TryGetValue(feedName, out var feed) || feed.AutoApply is null)
                return null;

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

            return new FeedMetadata
            {
                Feeds = new Dictionary<string, FeedDefinition>(existing.Feeds) { [feedName] = updatedFeed },
                Candles = existing.Candles,
            };
        }, ct);

        if (written) ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
        return written;
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
        var written = await UpdateWithRetry(path, existing =>
        {
            var updated = new Dictionary<string, FeedDefinition>(existing.Feeds);
            var removedAny = false;
            foreach (var id in feedIds)
                if (updated.Remove(id)) removedAny = true;
            if (!removedAny) return null;
            return new FeedMetadata { Feeds = updated, Candles = existing.Candles };
        }, ct);

        if (written) ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    public async Task EnsureCandleConfig(string assetDir, int decimalDigits, string interval, CancellationToken ct = default)
    {
        var path = FeedsJsonPath(assetDir);
        await UpdateWithRetry(path, existing =>
        {
            // Create-if-absent: preserve an existing recorded ScaleFactor. A post-3a rebuild
            // window can route a divergent exchangeInfo digit count here; overwriting would
            // silently corrupt the scale of already-written CSVs. Only intervals may grow.
            var scaleFactor = existing.Candles?.ScaleFactor ?? (decimal)Math.Pow(10, decimalDigits);
            var existingIntervals = existing.Candles?.Intervals ?? [];
            var updatedIntervals = existingIntervals.Contains(interval)
                ? existingIntervals
                : [..existingIntervals, interval];

            return new FeedMetadata
            {
                Feeds = existing.Feeds,
                Candles = new CandleConfig
                {
                    ScaleFactor = scaleFactor,
                    Intervals = updatedIntervals,
                },
            };
        }, ct);

        ManifestChanged?.Invoke(Path.GetFullPath(assetDir));
    }

    private static string FeedsJsonPath(string assetDir) =>
        Path.GetFullPath(Path.Combine(assetDir, "feeds.json"));

    private readonly record struct LoadResult(FeedMetadata? Metadata, string? ETag);

    private async Task<LoadResult> LoadWithEtag(string path, CancellationToken ct)
    {
        var stored = await _fs.ReadWithEtag(path, ct);
        if (stored is null) return new LoadResult(null, null);

        // Parse as JsonNode first so the validator can distinguish "property absent" from
        // "property == null" — JsonSerializer collapses both into null.
        var node = JsonNode.Parse(stored.Content);
        FeedMetadataValidator.ValidateOrThrow(node);
        var metadata = JsonSerializer.Deserialize<FeedMetadata>(stored.Content, JsonOptions);
        return new LoadResult(metadata, stored.ETag);
    }

    /// <summary>Returns <c>false</c> when the mutator returns <c>null</c> (no-op); <c>true</c> on a successful write.</summary>
    private async Task<bool> UpdateWithRetry(
        string path,
        Func<FeedMetadata, FeedMetadata?> mutator,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await LoadWithEtag(path, ct);
            var existing = current.Metadata ?? new FeedMetadata();
            var updated = mutator(existing);
            if (updated is null) return false;

            var json = SerializeAndValidate(updated);
            try
            {
                await _fs.WriteIfMatch(path, json, current.ETag, ct);
                return true;
            }
            catch (ConcurrencyConflictException) when (attempt < MaxAttempts - 1)
            {
                await Task.Delay(Backoff(attempt), ct);
            }
        }
    }

    private static string SerializeAndValidate(FeedMetadata metadata)
    {
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        var node = JsonNode.Parse(json);
        FeedMetadataValidator.ValidateOrThrow(node);
        return json;
    }

    private static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromMilliseconds(Random.Shared.Next(5, 21) * (1 << attempt));
}
