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
        if (manifest is null) return null;

        // Declared feeds (Side / Tick / explicit AltBar) live in `manifest.Feeds`.
        if (manifest.Feeds.TryGetValue(feedId, out var def))
            return def;

        // Synthesize a FeedDefinition for candle intervals so downstream consumers — most
        // importantly the /aggregation-options endpoint — can call EligibilityRules.ForSource
        // on a 1m/1h/1d source. Without this, the endpoint 404s and the new-aggregate form's
        // Type dropdown stays disabled. Mirrors the catalog-side projection in
        // BuildAssetEntries that surfaces these as OHLCV_TimeBar columns.
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

            var declaredFeedDict = manifest?.Feeds ?? new Dictionary<string, FeedDefinition>();
            var declaredFeeds = declaredFeedDict.Select(kvp => MapFeed(kvp.Key, kvp.Value));

            // Time-bar candles live in `manifest.Candles.Intervals`, separate from the `feeds`
            // dictionary (the candle pipeline is built-in; side feeds are pluggable). Synthesize
            // a FeedCatalogEntry per declared interval so the Data grid surfaces them as columns
            // and they're available as alt-bar source feeds. The on-disk CSVs already exist —
            // this is purely the read-time projection that was missing. Skip intervals already
            // claimed by a declared feed id so we never emit duplicates.
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

            yield return new AssetCatalogEntry(
                Exchange: asset.Exchange,
                Symbol: AssetPathConvention.DirectoryName(asset.Symbol, asset.Type),
                // Disambiguate spot vs perpetual/future labels. `asset.Symbol` alone collapses
                // BTCUSDT-spot and BTCUSDT-perp into identical user-facing rows. Suffix the
                // type for derivatives so the row is self-describing without making spot
                // labels noisy. Mirrors the directory-name convention (`BTCUSDT_perp`) but
                // uses a hyphen for human-readability.
                DisplayName: AssetTypes.IsFutures(asset.Type) ? $"{asset.Symbol}-perp" : asset.Symbol,
                Type: asset.Type,
                Feeds: feeds);
        }
    }

    private static FeedCatalogEntry MapFeed(string id, FeedDefinition def)
    {
        // Kind heuristic for entries in `manifest.Feeds` (auxiliary side feeds, ticks):
        //   1. Explicit `def.Kind` always wins.
        //   2. "ticks" by id convention → Tick.
        //   3. Everything else → Side.
        // The `Interval` field on declared feeds is the *polling cadence* (e.g., "15m"
        // for ls-ratio-global), NOT a candle interval. True OHLCV time bars are declared
        // only via `manifest.Candles.Intervals` and synthesized in BuildAssetEntries
        // above — never via the Feeds dictionary. So we MUST NOT promote a declared feed
        // to OHLCV_TimeBar based on its Interval field.
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
            // Normalize empty-string interval to null so the FE comparator's interval-aware
            // sort doesn't treat "" as duration 0. Cadence is preserved verbatim for Side
            // feeds (FE may surface it as a tooltip, etc.).
            Interval: string.IsNullOrEmpty(def.Interval) ? null : def.Interval,
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
