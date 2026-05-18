using System.Collections.Concurrent;
using AlgoTradeForge.Application.Threading;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Catalog;

/// <summary>
/// In-memory <see cref="IFeedCatalog"/> with version-suffixed cache keys: every
/// <c>ManifestChanged</c> event bumps a monotonic version, so new requests miss the cache
/// and rebuild from disk while old entries age out via TTL. Avoids the bookkeeping of
/// per-key invalidation while staying lock-free on the hot read path.
/// </summary>
public sealed class FeedCatalog : IFeedCatalog
{
    private readonly IOptionsMonitor<HistoryLoaderOptions> _options;
    private readonly ISchemaManager _schemaManager;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _ttl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _loadGates = new();

    private long _version;

    public FeedCatalog(
        IOptionsMonitor<HistoryLoaderOptions> options,
        ISchemaManager schemaManager,
        IMemoryCache cache)
    {
        _options = options;
        _schemaManager = schemaManager;
        _cache = cache;

        // Both this and the schema manager are singletons constructed at DI time, so the
        // subscription captures every mutation from the moment the catalog exists.
        _schemaManager.ManifestChanged += _ =>
        {
            Interlocked.Increment(ref _version);
            // Versioned cache keys are never reused after a bump, so the per-key gates from
            // the previous version are dead weight. Drop them to keep the map bounded.
            _loadGates.Clear();
        };
    }

    public Task<ExchangeListResponse> GetExchanges(CancellationToken ct = default) =>
        CachedAsync($"exchanges:{Version}", () =>
        {
            var groups = _options.CurrentValue.Assets
                .GroupBy(a => a.Exchange, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ExchangeSummary(g.Key, g.Count()))
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(new ExchangeListResponse(groups));
        });

    public Task<AssetListResponse> GetAssetsByExchange(string exchange, CancellationToken ct = default) =>
        CachedAsync($"assets:{exchange}:{Version}", async () =>
            new AssetListResponse(await BuildAssetEntries(exchange, ct)));

    public Task<AssetListResponse> GetAllAssets(CancellationToken ct = default) =>
        CachedAsync($"assets:all:{Version}", async () =>
            new AssetListResponse(await BuildAssetEntries(exchange: null, ct)));

    public async Task<AssetCatalogEntry?> GetAsset(string exchange, string assetSymbol, CancellationToken ct = default)
    {
        // No separate cache — the assets-by-exchange entry already contains this slice.
        var entries = await BuildAssetEntries(exchange, ct);
        return entries.FirstOrDefault(a =>
            string.Equals(a.Symbol, assetSymbol, StringComparison.Ordinal));
    }

    public async Task<FeedDefinition?> GetFeed(string exchange, string assetSymbol, string feedId, CancellationToken ct = default)
    {
        var asset = ResolveConfiguredAsset(exchange, assetSymbol);
        if (asset is null) return null;
        var assetDir = BackfillOrchestrator.ResolveAssetDir(_options.CurrentValue.DataRoot, asset);
        var manifest = await _schemaManager.Load(assetDir, ct);
        if (manifest is null) return null;

        // Declared feeds (Side / Tick / explicit AltBar) live in `manifest.Feeds`.
        if (manifest.Feeds.TryGetValue(feedId, out var def))
            return def;

        // Synthesize a FeedDefinition for candle intervals so the /aggregation-options endpoint
        // can resolve eligibility on a 1m/1h/1d source. Without this, the endpoint 404s and the
        // new-aggregate form's Type dropdown stays disabled.
        if (manifest.Candles?.Intervals.Contains(feedId) == true)
        {
            return new FeedDefinition
            {
                Kind = "OHLCV_TimeBar",
                Interval = feedId,
            };
        }

        return null;
    }

    // -------------------------------------------------------------------------

    private long Version => Interlocked.Read(ref _version);

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

    private AssetCollectionConfig? ResolveConfiguredAsset(string exchange, string assetSymbol) =>
        _options.CurrentValue.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                AssetPathConvention.DirectoryName(a.Symbol, a.Type),
                assetSymbol,
                StringComparison.Ordinal));

    private async Task<AssetCatalogEntry[]> BuildAssetEntries(string? exchange, CancellationToken ct)
    {
        var config = _options.CurrentValue;
        var assets = exchange is null
            ? config.Assets.ToArray()
            : config.Assets
                .Where(a => string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        // Per-asset loads are independent — fan out so the total cost is one round-trip
        // (max latency) instead of Σ(latencies). Matters once IFileStorage points at S3.
        var manifests = await Task.WhenAll(assets.Select(a =>
            _schemaManager.Load(BackfillOrchestrator.ResolveAssetDir(config.DataRoot, a), ct)));

        var result = new AssetCatalogEntry[assets.Length];
        for (var i = 0; i < assets.Length; i++)
        {
            var asset = assets[i];
            var manifest = manifests[i];

            var declaredFeedDict = manifest?.Feeds ?? new Dictionary<string, FeedDefinition>();
            var declaredFeeds = declaredFeedDict.Select(kvp => MapFeed(kvp.Key, kvp.Value));

            // Time-bar candles live in manifest.Candles.Intervals, separate from the feeds
            // dictionary. Surface them as catalog entries so the Data grid shows them and they
            // become available as alt-bar source feeds. Skip intervals already claimed by a
            // declared feed id to avoid duplicates.
            var candleFeeds = (manifest?.Candles?.Intervals ?? [])
                .Where(interval => !declaredFeedDict.ContainsKey(interval))
                .Select(interval => new FeedCatalogEntry(
                    Id: interval,
                    Kind: "OHLCV_TimeBar",
                    Interval: interval,
                    TypeCode: null,
                    ThresholdValue: null,
                    ThresholdUnit: null,
                    FirstBarTs: null,
                    LastBarTs: null,
                    Sidecar: null));

            var feeds = candleFeeds
                .Concat(declaredFeeds)
                .OrderBy(f => f, FeedOrder.Instance)
                .ToArray();

            result[i] = new AssetCatalogEntry(
                Exchange: asset.Exchange,
                Symbol: AssetPathConvention.DirectoryName(asset.Symbol, asset.Type),
                // Disambiguate spot vs perpetual labels — asset.Symbol alone collapses
                // BTCUSDT-spot and BTCUSDT-perp into identical rows.
                DisplayName: AssetTypes.IsFutures(asset.Type) ? $"{asset.Symbol}-perp" : asset.Symbol,
                Type: asset.Type,
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
