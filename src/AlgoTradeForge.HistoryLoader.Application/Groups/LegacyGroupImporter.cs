using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Application.Groups;

public static class LegacyGroupImporter
{
    // Ordered longest-first; "USD" is absent — symbols ending only in USD produce a warning.
    private static readonly string[] QuoteSuffixes =
        ["FDUSD", "TUSD", "USDT", "USDC", "BUSD", "BNB", "BTC", "ETH"];

    /// <summary>Pure. Converts HistoryLoaderOptions.Assets into groups named
    /// "legacy-{exchange}-{spot|perp}" (one per exchange×market present): symbols → canonical
    /// strings via reverse Binance mapping (BTCUSDT+perpetual → BTC/USDT-PERP: quote = longest
    /// match from [USDT,USDC,BUSD,BTC,ETH,BNB,FDUSD,TUSD] suffix — terse comment; unmappable
    /// symbol → skipped + returned in warnings); feeds with FeedCollectionConfig.Enabled == false
    /// are SKIPPED (not imported as on-demand — a disabled feed is not a desired feed); enabled
    /// feeds → GroupFeed(collect: Eager||!replenishable ? "eager" : "on-demand", intervals for
    /// candles from FeedCollectionConfig.Interval), historyStart = min asset HistoryStart
    /// formatted yyyy-MM. Lossy by design: DecimalDigits, GapThresholdMultiplier and per-feed
    /// HistoryStart overrides have no group representation — harmless in phase 2 (collectors
    /// still read appsettings; groups drive nothing yet); phase 3 must decide where
    /// decimalDigits comes from when appsettings retire.</summary>
    public static (IReadOnlyList<CollectionGroup> Groups, IReadOnlyList<string> Warnings) Convert(
        HistoryLoaderOptions options, ArchiveMaterializerRegistry replenishables)
    {
        var warnings = new List<string>();

        // Bucket assets by (exchange-lower, spot|perp).
        var buckets = new Dictionary<(string Exchange, string Market), List<AssetCollectionConfig>>();
        foreach (var asset in options.Assets)
        {
            var key = (asset.Exchange.ToLowerInvariant(), AssetTypes.IsFutures(asset.Type) ? "perp" : "spot");
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = [];
            list.Add(asset);
        }

        var groups = new List<CollectionGroup>();

        foreach (var ((exchange, market), assets) in buckets)
        {
            var assetType = market == "perp" ? AssetTypes.Perpetual : AssetTypes.Spot;

            // Reverse-map API symbols to canonical form; collect min historyStart.
            var symbols = new List<string>();
            var mappedStarts = new List<DateOnly>();
            foreach (var asset in assets)
            {
                if (!TryReverseMap(asset.Symbol, asset.Type, out var canonical))
                {
                    warnings.Add(
                        $"legacy-import: skipping '{asset.Symbol}' ({exchange}/{market}): " +
                        "no suffix match in [USDT,USDC,BUSD,BTC,ETH,BNB,FDUSD,TUSD]");
                    continue;
                }
                symbols.Add(canonical!);
                mappedStarts.Add(asset.HistoryStart);
            }

            if (symbols.Count == 0)
                continue;

            var historyStartStr = $"{mappedStarts.Min():yyyy-MM}";

            // Union enabled feeds across all assets in the bucket.
            var enabledFeeds = new HashSet<string>(StringComparer.Ordinal);
            var eagerFeeds = new HashSet<string>(StringComparer.Ordinal);
            var candleIntervals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var asset in assets)
            {
                foreach (var feed in asset.Feeds)
                {
                    if (!feed.Enabled) continue;
                    enabledFeeds.Add(feed.Name);
                    if (feed.Eager) eagerFeeds.Add(feed.Name);
                    if (feed.Name == FeedNames.Candles && !string.IsNullOrEmpty(feed.Interval))
                        candleIntervals.Add(feed.Interval);
                }
            }

            var feeds = new Dictionary<string, GroupFeed>();
            foreach (var feedName in enabledFeeds)
            {
                var replenishable = replenishables.IsReplenishable(exchange, feedName, assetType);
                var collect = eagerFeeds.Contains(feedName) || !replenishable ? "eager" : "on-demand";

                IReadOnlyList<string>? intervals = null;
                if (feedName == FeedNames.Candles)
                {
                    if (candleIntervals.Count == 0) continue; // no intervals → would fail GroupValidator
                    intervals = candleIntervals.OrderBy(i => i, StringComparer.Ordinal).ToList();
                }

                feeds[feedName] = new GroupFeed(collect, intervals, null);
            }

            groups.Add(new CollectionGroup(
                Name: $"legacy-{exchange}-{market}",
                Enabled: true,
                Exchanges: [exchange],
                Assets: new GroupAssets(symbols, historyStartStr),
                Feeds: feeds,
                Derived: null,
                SymbolOverrides: null));
        }

        return (groups, warnings);
    }

    private static bool TryReverseMap(string apiSymbol, string assetType, out string? canonical)
    {
        canonical = null;
        foreach (var suffix in QuoteSuffixes)
        {
            if (!apiSymbol.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            var baseToken = apiSymbol[..^suffix.Length].ToUpperInvariant();
            // Same [A-Z0-9] token rule as CanonicalSymbolParser — reject here so a malformed
            // config symbol becomes a per-symbol warning, not a GroupValidationException at Put.
            if (baseToken.Length == 0 || baseToken.Length > 20
                || !baseToken.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
                return false;
            canonical = AssetTypes.IsFutures(assetType)
                ? $"{baseToken}/{suffix}-PERP"
                : $"{baseToken}/{suffix}";
            return true;
        }
        return false;
    }
}
