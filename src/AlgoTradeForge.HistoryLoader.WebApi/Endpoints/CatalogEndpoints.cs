using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class CatalogEndpoints
{
    public static WebApplication MapCatalogEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/exchanges", (IFeedCatalog catalog) =>
            Results.Json(catalog.GetExchanges()));

        v1.MapGet("/exchanges/{exchange}/assets", (string exchange, IFeedCatalog catalog) =>
            Results.Json(catalog.GetAssetsByExchange(exchange)));

        v1.MapGet("/assets", (IFeedCatalog catalog) =>
            Results.Json(catalog.GetAllAssets()));

        v1.MapGet("/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/status",
            (string exchange, string asset, string feedId, IFeedCatalog catalog) =>
            {
                var def = catalog.GetFeed(exchange, asset, feedId);
                if (def is null)
                    return Results.NotFound(new { error = "feed not found", exchange, asset, feed_id = feedId });
                return Results.Json(new { feed_id = feedId, definition = def });
            });

        v1.MapGet("/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/aggregation-options",
            (string exchange, string asset, string feedId,
             IFeedCatalog catalog, IOptionsMonitor<HistoryLoaderOptions> options) =>
            {
                var entry = catalog.GetAsset(exchange, asset);
                if (entry is null)
                    return Results.NotFound(new { error = "asset not found", exchange, asset });

                var def = catalog.GetFeed(exchange, asset, feedId);
                if (def is null)
                    return Results.NotFound(new { error = "feed not found", exchange, asset, feed_id = feedId });

                var hasCandleExt = entry.Feeds.Any(f =>
                    string.Equals(f.Id, "candle-ext", StringComparison.Ordinal));

                var eligibility = EligibilityRules.ForSource(def, entry.Type, hasCandleExt);

                // Q-3 — per-asset, per-unit threshold floor. The catalog endpoint returns ONE
                // conservative `min` (the largest of the per-unit minima for the asset's scale)
                // so any FE form that wires the bound is safe regardless of which unit the user
                // selects. The TRD §5.3-aligned per-eligible-type bounds shape is a follow-up
                // (see plan: out-of-scope deferred). Falls back to the canonical 1m floor when
                // the asset is not configured in HistoryLoaderOptions (catalog-only assets).
                var assetConfig = options.CurrentValue.Assets.FirstOrDefault(a =>
                    string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(AssetPathConvention.DirectoryName(a.Symbol, a.Type), asset, StringComparison.Ordinal));
                var conservativeMin = assetConfig is null
                    ? 1m
                    : ConservativeThresholdFloor(AssetScaleContextFactory.FromDecimalDigits(assetConfig.DecimalDigits));

                // Anonymous-object property names pass through verbatim — System.Text.Json's
                // PropertyNamingPolicy only rewrites declared CLR member names. Spell snake_case
                // explicitly here so the wire shape matches the TRD §5 schema.
                return Results.Json(new
                {
                    feed_id = feedId,
                    kind = def.Kind ?? (def.Interval is not null ? "OHLCV_TimeBar" : "Side"),
                    eligible_types = eligibility.EligibleTypes,
                    ineligible_types = eligibility.IneligibleTypes,
                    threshold_bounds = new { min = conservativeMin, max = (decimal?)null },
                    warnings = eligibility.Warnings,
                });
            });

        return app;
    }

    /// <summary>
    /// Q-3 — picks the largest per-unit minimum across the four supported threshold units.
    /// "Conservative" because any user input above this floor is guaranteed to satisfy
    /// every per-unit-floor check, regardless of which type/unit the form chooses. Less
    /// informative than a per-unit map, but matches the existing single-`min` wire shape
    /// without a breaking FE change. See `ThresholdResolver.MinimumAbsolute` for per-unit math.
    /// </summary>
    private static decimal ConservativeThresholdFloor(ScaleContext scale) =>
        new[]
        {
            ThresholdResolver.MinimumAbsolute("base_asset", scale),
            ThresholdResolver.MinimumAbsolute("quote_asset", scale),
            ThresholdResolver.MinimumAbsolute("trades", scale),
            ThresholdResolver.MinimumAbsolute("price", scale),
        }.Max();
}
