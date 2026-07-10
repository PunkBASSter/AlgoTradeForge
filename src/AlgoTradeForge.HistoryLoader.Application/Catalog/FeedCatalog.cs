using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Catalog;

/// <summary>
/// Index-backed <see cref="IFeedCatalog"/>: list and asset lookups read from
/// <see cref="IHistoryIndex"/> (SQLite) rather than crawling the filesystem.
/// Lists are eventually consistent — the maintenance queue (single-digit ms typical)
/// propagates manifest changes before they are visible here. The FE already refetches
/// on job completion, so the lag is acceptable (spec §3.3).
/// <see cref="GetFeed"/> continues to read via <see cref="ISchemaManager"/> directly:
/// aggregation flows call it immediately after a manifest mutation and must not race
/// the async index queue.
/// </summary>
public sealed class FeedCatalog : IFeedCatalog
{
    private readonly IHistoryIndex _index;
    private readonly IOptionsMonitor<HistoryLoaderOptions> _options;
    private readonly ISchemaManager _schemaManager;

    public FeedCatalog(
        IHistoryIndex index,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ISchemaManager schemaManager)
    {
        _index = index;
        _options = options;
        _schemaManager = schemaManager;
    }

    public async Task<ExchangeListResponse> GetExchanges(CancellationToken ct = default)
    {
        var rows = await _index.ListAssets(ct: ct);
        var groups = rows
            .GroupBy(r => r.Exchange, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ExchangeSummary(g.Key, g.Count()))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToArray();
        return new ExchangeListResponse(groups);
    }

    public async Task<AssetListResponse> GetAssetsByExchange(string exchange, CancellationToken ct = default)
    {
        var rows = await _index.ListAssets(exchange, ct);
        return new AssetListResponse(rows.Select(BuildEntryFromRow).ToArray());
    }

    public async Task<AssetListResponse> GetAllAssets(CancellationToken ct = default)
    {
        var rows = await _index.ListAssets(ct: ct);
        return new AssetListResponse(rows.Select(BuildEntryFromRow).ToArray());
    }

    public async Task<AssetCatalogEntry?> GetAsset(string exchange, string assetSymbol, CancellationToken ct = default)
    {
        var row = await _index.GetAsset(exchange, assetSymbol, ct);
        return row is null ? null : BuildEntryFromRow(row);
    }

    public async Task<FeedDefinition?> GetFeed(string exchange, string assetSymbol, string feedId, CancellationToken ct = default)
    {
        if (!TryResolveAssetDir(_options.CurrentValue.DataRoot, exchange, assetSymbol, out var assetDir))
            return null;

        var manifest = await _schemaManager.Load(assetDir, ct);
        if (manifest is null) return null;

        if (manifest.Feeds.TryGetValue(feedId, out var def))
            return def;

        if (manifest.Candles?.Intervals.Contains(feedId) == true)
            return new FeedDefinition { Kind = "OHLCV_TimeBar", Interval = feedId };

        return null;
    }

    // -------------------------------------------------------------------------

    private static AssetCatalogEntry BuildEntryFromRow(AssetIndexRow row)
    {
        FeedMetadata? manifest = null;
        if (!string.IsNullOrWhiteSpace(row.ManifestJson))
            manifest = JsonSerializer.Deserialize<FeedMetadata>(row.ManifestJson, ManifestJson.Options);

        var declaredFeedDict = manifest?.Feeds ?? new Dictionary<string, FeedDefinition>();
        var declaredFeeds = declaredFeedDict.Select(kvp => MapFeed(kvp.Key, kvp.Value));

        var candleFeeds = (manifest?.Candles?.Intervals ?? [])
            .Where(interval => !declaredFeedDict.ContainsKey(interval))
            .Select(interval => new FeedCatalogEntry(
                Id: interval, Kind: "OHLCV_TimeBar", Interval: interval,
                TypeCode: null, ThresholdValue: null, ThresholdUnit: null,
                FirstBarTs: null, LastBarTs: null, Sidecar: null));

        var feeds = candleFeeds.Concat(declaredFeeds).OrderBy(f => f, FeedOrder.Instance).ToArray();

        return new AssetCatalogEntry(
            Exchange: row.Exchange,
            Symbol: row.Dir,
            DisplayName: AssetTypes.IsFutures(row.Type) ? $"{row.Symbol}-perp" : row.Symbol,
            Type: row.Type,
            Feeds: feeds);
    }

    // exchange/assetSymbol arrive from user-controlled route params. Confine the resolved dir
    // to exactly {exchange}/{asset} under DataRoot so "..", embedded separators, or an absolute
    // path can't read a feeds.json outside the intended asset directory.
    private static bool TryResolveAssetDir(string dataRoot, string exchange, string assetSymbol, out string assetDir)
    {
        assetDir = Path.Combine(dataRoot, exchange, assetSymbol);
        var rel = Path.GetRelativePath(Path.GetFullPath(dataRoot), Path.GetFullPath(assetDir));
        if (Path.IsPathRooted(rel)) return false;
        var segments = rel.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 && Array.TrueForAll(segments, s => s != "..");
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
