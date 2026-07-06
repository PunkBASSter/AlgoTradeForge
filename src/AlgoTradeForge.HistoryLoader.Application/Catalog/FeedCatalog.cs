using System.Collections.Concurrent;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Storage.Threading;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Catalog;

/// <summary>
/// Filesystem-sourced <see cref="IFeedCatalog"/>: one entry per <c>feeds.json</c> found under
/// <c>DataRoot</c>. Version-suffixed cache keys — <c>ManifestChanged</c> or an explicit
/// <see cref="Refresh"/> bumps the version so new requests rescan. Type is heuristic
/// (see <see cref="AssetDirectoryClassifier"/>).
/// </summary>
public sealed class FeedCatalog : IFeedCatalog
{
    private readonly IFileStorage _storage;
    private readonly IOptionsMonitor<HistoryLoaderOptions> _options;
    private readonly ISchemaManager _schemaManager;
    private readonly IMemoryCache _cache;
    // Refresh-gated (not per-request): a full feeds.json scan touches every file under
    // DataRoot, so hold results until a version bump rather than a short TTL.
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadGates = new();

    private long _version;

    public FeedCatalog(
        IFileStorage storage,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ISchemaManager schemaManager,
        IMemoryCache cache)
    {
        _storage = storage;
        _options = options;
        _schemaManager = schemaManager;
        _cache = cache;

        _schemaManager.ManifestChanged += _ =>
        {
            Interlocked.Increment(ref _version);
            _loadGates.Clear();
        };
    }

    public void Refresh()
    {
        Interlocked.Increment(ref _version);
        _loadGates.Clear();
    }

    public Task<ExchangeListResponse> GetExchanges(CancellationToken ct = default) =>
        CachedAsync($"exchanges:{Version}", async () =>
        {
            var dirs = await ScanAssetDirs(ct);
            var groups = dirs
                .GroupBy(d => d.Exchange, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ExchangeSummary(g.Key, g.Count()))
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToArray();
            return new ExchangeListResponse(groups);
        });

    public Task<AssetListResponse> GetAssetsByExchange(string exchange, CancellationToken ct = default) =>
        CachedAsync($"assets:{exchange}:{Version}", async () =>
            new AssetListResponse(await BuildAssetEntries(exchange, ct)));

    public Task<AssetListResponse> GetAllAssets(CancellationToken ct = default) =>
        CachedAsync($"assets:all:{Version}", async () =>
            new AssetListResponse(await BuildAssetEntries(exchange: null, ct)));

    public async Task<AssetCatalogEntry?> GetAsset(string exchange, string assetSymbol, CancellationToken ct = default)
    {
        var entries = await BuildAssetEntries(exchange, ct);
        return entries.FirstOrDefault(a => string.Equals(a.Symbol, assetSymbol, StringComparison.Ordinal));
    }

    public async Task<FeedDefinition?> GetFeed(string exchange, string assetSymbol, string feedId, CancellationToken ct = default)
    {
        var assetDir = Path.Combine(_options.CurrentValue.DataRoot, exchange, assetSymbol);
        var manifest = await _schemaManager.Load(assetDir, ct);
        if (manifest is null) return null;

        if (manifest.Feeds.TryGetValue(feedId, out var def))
            return def;

        if (manifest.Candles?.Intervals.Contains(feedId) == true)
            return new FeedDefinition { Kind = "OHLCV_TimeBar", Interval = feedId };

        return null;
    }

    // -------------------------------------------------------------------------

    private long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// One (exchange, dir) per <c>feeds.json</c> under DataRoot. feeds.json is the per-asset
    /// marker (both the importer and FeedSchemaManager write exactly one per dir), so scanning
    /// it — rather than candle files — yields ~one key per asset instead of one per partition.
    /// </summary>
    private async Task<List<(string Exchange, string Dir)>> ScanAssetDirs(CancellationToken ct)
    {
        var dataRoot = _options.CurrentValue.DataRoot;
        var seen = new HashSet<(string, string)>();
        var result = new List<(string, string)>();
        await foreach (var key in _storage.ListKeys(dataRoot, suffix: "feeds.json", recursive: true, ct))
        {
            var segments = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3) continue; // …/{exchange}/{dir}/feeds.json
            var exchange = segments[^3];
            var dir = segments[^2];
            if (seen.Add((exchange, dir))) result.Add((exchange, dir));
        }
        result.Sort((a, b) =>
        {
            var cmp = string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        });
        return result;
    }

    private async Task<AssetCatalogEntry[]> BuildAssetEntries(string? exchange, CancellationToken ct)
    {
        var dataRoot = _options.CurrentValue.DataRoot;
        var dirs = await ScanAssetDirs(ct);
        if (exchange is not null)
            dirs = dirs.Where(d => string.Equals(d.Exchange, exchange, StringComparison.OrdinalIgnoreCase)).ToList();

        var manifests = await Task.WhenAll(dirs.Select(d =>
            _schemaManager.Load(Path.Combine(dataRoot, d.Exchange, d.Dir), ct)));

        var result = new AssetCatalogEntry[dirs.Count];
        for (var i = 0; i < dirs.Count; i++)
        {
            var (exchangeName, dir) = dirs[i];
            var manifest = manifests[i];
            var (symbol, type) = AssetDirectoryClassifier.Classify(exchangeName, dir);

            var declaredFeedDict = manifest?.Feeds ?? new Dictionary<string, FeedDefinition>();
            var declaredFeeds = declaredFeedDict.Select(kvp => MapFeed(kvp.Key, kvp.Value));

            var candleFeeds = (manifest?.Candles?.Intervals ?? [])
                .Where(interval => !declaredFeedDict.ContainsKey(interval))
                .Select(interval => new FeedCatalogEntry(
                    Id: interval, Kind: "OHLCV_TimeBar", Interval: interval,
                    TypeCode: null, ThresholdValue: null, ThresholdUnit: null,
                    FirstBarTs: null, LastBarTs: null, Sidecar: null));

            var feeds = candleFeeds.Concat(declaredFeeds).OrderBy(f => f, FeedOrder.Instance).ToArray();

            result[i] = new AssetCatalogEntry(
                Exchange: exchangeName,
                Symbol: dir,
                DisplayName: AssetTypes.IsFutures(type) ? $"{symbol}-perp" : symbol,
                Type: type,
                Feeds: feeds);
        }
        return result;
    }

    private static FeedCatalogEntry MapFeed(string id, FeedDefinition def)
    {
        // Kind for entries in manifest.Feeds: explicit def.Kind wins; id "ticks" → Tick;
        // otherwise Side. Note: def.Interval here is polling cadence, not a candle interval —
        // true OHLCV time bars come only from manifest.Candles.Intervals.
        string kind;
        if (def.Kind is not null)
            kind = def.Kind;
        else if (string.Equals(id, "ticks", StringComparison.Ordinal))
            kind = "Tick";
        else
            kind = "Side";

        return new FeedCatalogEntry(
            Id: id,
            Kind: kind,
            // Normalize empty interval to null so the FE comparator doesn't treat "" as duration 0.
            Interval: string.IsNullOrEmpty(def.Interval) ? null : def.Interval,
            TypeCode: def.Type?.Code,
            ThresholdValue: def.Threshold?.Value,
            ThresholdUnit: def.Threshold?.Unit,
            FirstBarTs: def.FirstBarTs,
            LastBarTs: def.LastBarTs,
            Sidecar: def.Sidecar);
    }

    private async Task<T> CachedAsync<T>(string key, Func<Task<T>> factory) where T : class
    {
        // Fast path: avoid the gate on a hot cache hit.
        if (_cache.TryGetValue(key, out T? hit) && hit is not null) return hit;

        // Single-flight per key. Without this, concurrent miss-readers each invoke factory
        // (which awaits per-asset manifest I/O), fanning out into N parallel file reads per
        // HTTP burst — fine on local FS, painful on S3 latency.
        var gate = _loadGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        using var _ = await gate.LockAsync(CancellationToken.None);

        if (_cache.TryGetValue(key, out hit) && hit is not null) return hit;

        using var entry = _cache.CreateEntry(key);
        entry.AbsoluteExpirationRelativeToNow = _ttl;
        var result = await factory();
        entry.Value = result;
        return result;
    }

    /// <summary>
    /// Display order: time bars (by interval), then alt bars (by type, then threshold ascending),
    /// then ticks, then side feeds.
    /// </summary>
    private sealed class FeedOrder : IComparer<FeedCatalogEntry>
    {
        public static readonly FeedOrder Instance = new();

        public int Compare(FeedCatalogEntry? x, FeedCatalogEntry? y)
        {
            if (x is null || y is null) return 0;

            var bucketDiff = Bucket(x).CompareTo(Bucket(y));
            if (bucketDiff != 0) return bucketDiff;

            return Bucket(x) switch
            {
                1 => string.CompareOrdinal(x.Interval ?? x.Id, y.Interval ?? y.Id),
                2 => CompareAltBar(x, y),
                _ => string.CompareOrdinal(x.Id, y.Id),
            };
        }

        private static int Bucket(FeedCatalogEntry f) => f.Kind switch
        {
            "OHLCV_TimeBar" => 1,
            "OHLCV_AltBar"  => 2,
            "Tick"          => 3,
            _               => 4,
        };

        private static int CompareAltBar(FeedCatalogEntry x, FeedCatalogEntry y)
        {
            var typeDiff = string.CompareOrdinal(x.TypeCode, y.TypeCode);
            if (typeDiff != 0) return typeDiff;
            return Nullable.Compare(x.ThresholdValue, y.ThresholdValue);
        }
    }
}
