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
        _schemaManager.ManifestChanged += _ => Interlocked.Increment(ref _version);
    }

    public ExchangeListResponse GetExchanges() =>
        Cached($"exchanges:{Version}", () =>
        {
            var groups = _options.CurrentValue.Assets
                .GroupBy(a => a.Exchange, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ExchangeSummary(g.Key, g.Count()))
                .OrderBy(e => e.Name, StringComparer.Ordinal)
                .ToArray();
            return new ExchangeListResponse(groups);
        });

    public AssetListResponse GetAssetsByExchange(string exchange) =>
        Cached($"assets:{exchange}:{Version}", () =>
            new AssetListResponse(BuildAssetEntries(exchange).ToArray()));

    public AssetListResponse GetAllAssets() =>
        Cached($"assets:all:{Version}", () =>
            new AssetListResponse(BuildAssetEntries(exchange: null).ToArray()));

    public AssetCatalogEntry? GetAsset(string exchange, string assetSymbol)
    {
        // Don't cache the per-asset slice independently — the assets-by-exchange entry already
        // has it, and computing both burns memory for the same data.
        return BuildAssetEntries(exchange).FirstOrDefault(a =>
            string.Equals(a.Symbol, assetSymbol, StringComparison.Ordinal));
    }

    public FeedDefinition? GetFeed(string exchange, string assetSymbol, string feedId)
    {
        var asset = ResolveConfiguredAsset(exchange, assetSymbol);
        if (asset is null) return null;
        var assetDir = BackfillOrchestrator.ResolveAssetDir(_options.CurrentValue.DataRoot, asset);
        var manifest = _schemaManager.Load(assetDir);
        return manifest?.Feeds.TryGetValue(feedId, out var def) == true ? def : null;
    }

    // -------------------------------------------------------------------------

    private long Version => Interlocked.Read(ref _version);

    private T Cached<T>(string key, Func<T> factory)
    {
        return _cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = _ttl;
            return factory();
        })!;
    }

    private AssetCollectionConfig? ResolveConfiguredAsset(string exchange, string assetSymbol) =>
        _options.CurrentValue.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                AssetPathConvention.DirectoryName(a.Symbol, a.Type),
                assetSymbol,
                StringComparison.Ordinal));

    private IEnumerable<AssetCatalogEntry> BuildAssetEntries(string? exchange)
    {
        var config = _options.CurrentValue;
        var assets = config.Assets.AsEnumerable();
        if (exchange is not null)
        {
            assets = assets.Where(a => string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var asset in assets)
        {
            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
            var manifest = _schemaManager.Load(assetDir);
            var feeds = (manifest?.Feeds ?? new Dictionary<string, FeedDefinition>())
                .Select(kvp => MapFeed(kvp.Key, kvp.Value))
                .OrderBy(f => f, FeedOrder.Instance)
                .ToArray();

            yield return new AssetCatalogEntry(
                Exchange: asset.Exchange,
                Symbol: AssetPathConvention.DirectoryName(asset.Symbol, asset.Type),
                DisplayName: asset.Symbol,
                Type: asset.Type,
                Feeds: feeds);
        }
    }

    private static FeedCatalogEntry MapFeed(string id, FeedDefinition def)
    {
        // Legacy entries leave Kind null but populate Interval — treat those as time bars
        // so the FE doesn't see an empty kind on legacy feeds.
        var kind = def.Kind ?? (def.Interval is not null ? "OHLCV_TimeBar" : "Side");
        return new FeedCatalogEntry(
            Id: id,
            Kind: kind,
            Interval: def.Interval,
            TypeCode: def.Type?.Code,
            ThresholdValue: def.Threshold?.Value,
            ThresholdUnit: def.Threshold?.Unit,
            FirstBarTs: def.FirstBarTs,
            LastBarTs: def.LastBarTs,
            Sidecar: def.Sidecar);
    }

    /// <summary>
    /// Display order: time bars first (sorted by interval), then alt bars (by type then
    /// threshold ascending), then ticks, then side feeds (TRD §5.1 / TRD §10.1 column order).
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
