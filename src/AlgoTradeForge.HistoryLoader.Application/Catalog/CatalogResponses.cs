namespace AlgoTradeForge.HistoryLoader.Application.Catalog;

/// <summary>Response shape for <c>GET /api/v1/exchanges</c> (TRD §5.1).</summary>
public sealed record ExchangeListResponse(IReadOnlyList<ExchangeSummary> Exchanges);

public sealed record ExchangeSummary(string Name, int AssetCount);

/// <summary>
/// Response shape for <c>GET /api/v1/exchanges/{exchange}/assets</c> and
/// <c>GET /api/v1/assets</c>. Each asset carries the configured metadata plus its
/// per-asset feed inventory merged from <c>feeds.json</c>.
/// </summary>
public sealed record AssetListResponse(IReadOnlyList<AssetCatalogEntry> Assets);

public sealed record AssetCatalogEntry(
    string Exchange,
    string Symbol,                    // directory name, e.g. "BTCUSDT_perp"
    string DisplayName,               // raw symbol, e.g. "BTCUSDT"
    string Type,                      // "Crypto" | "CryptoPerpetual" | ...
    IReadOnlyList<FeedCatalogEntry> Feeds);

public sealed record FeedCatalogEntry(
    string Id,                        // e.g. "1m" | "EqV_1m_1000" | "funding-rate"
    string Kind,                      // "OHLCV_TimeBar" | "OHLCV_AltBar" | "Tick" | "Side"
    string? Interval,                 // populated for time-bar feeds
    string? TypeCode,                 // populated for alt-bar feeds, e.g. "EqV"
    decimal? ThresholdValue,          // populated for alt-bar feeds, in canonical units
    string? ThresholdUnit,
    string? FirstBarTs,
    string? LastBarTs,
    string? Sidecar);
