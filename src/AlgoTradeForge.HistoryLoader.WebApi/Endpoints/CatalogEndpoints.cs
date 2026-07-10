using System.Text.Json;
using AlgoTradeForge.HistoryLoader.Application.Aggregation;
using AlgoTradeForge.HistoryLoader.Application.Catalog;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class CatalogEndpoints
{
    public static WebApplication MapCatalogEndpoints(this WebApplication app)
    {
        var v1 = app.MapGroup("/api/v1");

        v1.MapGet("/exchanges", async (IFeedCatalog catalog, CancellationToken ct) =>
            Results.Json(await catalog.GetExchanges(ct)));

        v1.MapGet("/exchanges/{exchange}/assets", async (string exchange, IFeedCatalog catalog, CancellationToken ct) =>
            Results.Json(await catalog.GetAssetsByExchange(exchange, ct)));

        v1.MapGet("/assets", async (IFeedCatalog catalog, CancellationToken ct) =>
            Results.Json(await catalog.GetAllAssets(ct)));

        v1.MapPost("/catalog/refresh", async (IHistoryIndex index, IIndexMaintenance maintenance, CancellationToken ct) =>
        {
            // At 12k assets a rebuild is a long crawl — always a job, never synchronous (spec §3.3).
            var active = await index.GetActiveJob("rebuild", ct);
            if (active is not null)
                return Results.Accepted($"/api/v1/index/jobs/{active.Id}", new { job_id = active.Id });

            var jobId = await index.CreateJob("rebuild", ct);
            maintenance.Enqueue(new IndexWork.Rebuild(jobId));
            return Results.Accepted($"/api/v1/index/jobs/{jobId}", new { job_id = jobId });
        });

        v1.MapGet("/index/jobs/{id}", async (string id, IHistoryIndex index, CancellationToken ct) =>
        {
            var job = await index.GetJob(id, ct);
            return job is null
                ? Results.NotFound(new { error = "job not found", id })
                : Results.Ok(new { id = job.Id, kind = job.Kind, state = job.State,
                    progress = JsonDocument.Parse(job.ProgressJson).RootElement,
                    error = job.Error });
        });

        v1.MapGet("/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/status",
            async (string exchange, string asset, string feedId, IFeedCatalog catalog, CancellationToken ct) =>
            {
                var def = await catalog.GetFeed(exchange, asset, feedId, ct);
                if (def is null)
                    return Results.NotFound(new { error = "feed not found", exchange, asset, feed_id = feedId });
                return Results.Json(new { feed_id = feedId, definition = def });
            });

        v1.MapGet("/exchanges/{exchange}/assets/{asset}/feeds/{feedId}/aggregation-options",
            async (string exchange, string asset, string feedId, IFeedCatalog catalog, CancellationToken ct) =>
            {
                var entry = await catalog.GetAsset(exchange, asset, ct);
                if (entry is null)
                    return Results.NotFound(new { error = "asset not found", exchange, asset });

                var def = await catalog.GetFeed(exchange, asset, feedId, ct);
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
