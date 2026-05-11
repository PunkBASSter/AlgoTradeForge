using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Domain;

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
            (string exchange, string asset, string feedId, IFeedCatalog catalog) =>
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

                // Anonymous-object property names pass through verbatim — STJ's
                // PropertyNamingPolicy only rewrites declared CLR member names; spell snake_case
                // explicitly to match the wire schema.
                return Results.Json(new
                {
                    feed_id = feedId,
                    kind = def.Kind ?? (def.Interval is not null ? "OHLCV_TimeBar" : "Side"),
                    eligible_types = eligibility.EligibleTypes,
                    ineligible_types = eligibility.IneligibleTypes,
                    threshold_bounds = new { min = MinimumThresholdAbsolute(), max = (decimal?)null },
                    warnings = eligibility.Warnings,
                });
            });

        return app;
    }

    /// <summary>
    /// Per-eligible-type, per-unit bounds are deferred. Returns the canonical 1-unit floor;
    /// <c>ThresholdResolver</c> enforces the real per-unit, per-asset minimum at request time,
    /// so this is purely informational for the form side.
    /// </summary>
    private static decimal MinimumThresholdAbsolute() => 1m;
}
