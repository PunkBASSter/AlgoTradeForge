using System.Globalization;
using System.Text.Json;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class CoverageEndpoints
{
    public static WebApplication MapCoverageEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/v1").MapGet("/coverage", GetCoverage);
        return app;
    }

    // internal for direct endpoint-level testing (InternalsVisibleTo)
    internal static async Task<IResult> GetCoverage(
        string exchange,
        string symbol,
        [Microsoft.AspNetCore.Mvc.FromQuery(Name = "asset_type")] string assetType,
        IOptionsMonitor<HistoryLoaderOptions> options,
        IHistoryIndex index,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!FeedIdValidator.TryValidatePathComponent(exchange, out var exchangeErr))
            return Results.Json(
                new { error = "invalid_path_component", message = exchangeErr },
                statusCode: StatusCodes.Status422UnprocessableEntity);
        if (!FeedIdValidator.TryValidatePathComponent(symbol, out var symbolErr))
            return Results.Json(
                new { error = "invalid_path_component", message = symbolErr },
                statusCode: StatusCodes.Status422UnprocessableEntity);

        // Guard before AssetPathConvention.DirectoryName — its default switch arm throws
        // ArgumentException on unknown types, which would surface as an unhandled 500.
        if (!LoadRequestValidator.IsKnownAssetType(assetType))
            return Results.Json(
                new
                {
                    error = "unknown_asset_type",
                    message = $"Unknown asset type '{assetType}'. Valid types: {string.Join(", ", AssetTypes.All)}.",
                },
                statusCode: StatusCodes.Status422UnprocessableEntity);

        var opts = options.CurrentValue;
        var dir = AssetPathConvention.DirectoryName(symbol, assetType);
        var assetDir = Path.Combine(opts.DataRoot, exchange, dir);

        var asset = await index.GetAsset(exchange, dir, ct);
        if (asset is null)
            return Results.Ok(new { asset_dir = assetDir, feeds = Array.Empty<object>() });

        var manifest = JsonSerializer.Deserialize<FeedMetadata>(asset.ManifestJson, ManifestJson.Options)!;

        var statusRows = await index.GetFeedStatuses(exchange, dir, ct);
        var statusDict = statusRows.ToDictionary(r => (r.FeedName, r.Interval));

        var nowMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var feedEntries = new List<object>();
        var candleCoveredByInterval = new Dictionary<string, IReadOnlyList<string>>();

        // Candle intervals from Candles.Intervals
        foreach (var interval in manifest.Candles?.Intervals ?? [])
        {
            var built = await BuildFeedEntry(exchange, dir, FeedNames.Candles, interval, index, statusDict, nowMs, ct);
            if (built is not { } b) continue;
            feedEntries.Add(b.Entry);
            candleCoveredByInterval[interval] = b.CoveredMonths;
        }

        // Declared feeds with an interval (skip alt-bar/tick/side entries without intervals)
        foreach (var (feedName, def) in manifest.Feeds)
        {
            if (string.IsNullOrEmpty(def.Interval)) continue;

            // candle-ext has no materializer (side-output of candles) → mirror candles' coverage
            // for the same interval rather than compute its own possibly-partial partitions.
            if (feedName == FeedNames.CandleExt)
            {
                var shadow = await BuildCandleExtShadow(exchange, dir, def.Interval,
                    candleCoveredByInterval, statusDict, index, ct);
                if (shadow is not null) feedEntries.Add(shadow);
                continue;
            }

            var built = await BuildFeedEntry(exchange, dir, feedName, def.Interval, index, statusDict, nowMs, ct);
            if (built is { } b) feedEntries.Add(b.Entry);
        }

        // Interval-less feeds: coverage is CompleteMonths marker, not the partition row count.
        // Presence = status row exists (unchanged semantics).
        foreach (var feed in new[] { FeedNames.Ticks, FeedNames.FundingRate })
        {
            if (!statusDict.TryGetValue((feed, ""), out var row)) continue;
            var completeMonths = JsonSerializer.Deserialize<string[]>(row.CompleteMonthsJson, ManifestJson.Options) ?? [];
            feedEntries.Add(new
            {
                feed_name = feed, interval = "",
                covered_months = completeMonths.OrderBy(m => m, StringComparer.Ordinal).ToArray(),
                first_timestamp = row.FirstTs,
                last_timestamp = row.LastTs,
            });
        }

        return Results.Ok(new { asset_dir = assetDir, feeds = feedEntries });
    }

    private static async Task<object?> BuildCandleExtShadow(
        string exchange, string dir, string interval,
        IReadOnlyDictionary<string, IReadOnlyList<string>> candleCoveredByInterval,
        Dictionary<(string FeedName, string Interval), FeedStatusIndexRow> statusDict,
        IHistoryIndex index, CancellationToken ct)
    {
        // Presence: status row OR month rows for (candle-ext, interval).
        statusDict.TryGetValue((FeedNames.CandleExt, interval), out var statusRow);
        var months = await index.GetMonths(exchange, dir, FeedNames.CandleExt, interval, ct);
        if (statusRow is null && months.Count == 0) return null;

        var covered = candleCoveredByInterval.TryGetValue(interval, out var cm) ? cm : [];
        return new
        {
            feed_name = FeedNames.CandleExt,
            interval,
            covered_months = covered.ToArray(),
            first_timestamp = statusRow?.FirstTs,
            last_timestamp = statusRow?.LastTs,
        };
    }

    private static async Task<(object Entry, IReadOnlyList<string> CoveredMonths)?> BuildFeedEntry(
        string exchange, string dir, string feedName, string interval,
        IHistoryIndex index,
        Dictionary<(string FeedName, string Interval), FeedStatusIndexRow> statusDict,
        long nowMs, CancellationToken ct)
    {
        // Presence rule (spec D6): status row OR month rows — never require both.
        // Static equity assets have month partitions but typically no status_*.json.
        statusDict.TryGetValue((feedName, interval), out var statusRow);
        var months = await index.GetMonths(exchange, dir, feedName, interval, ct);
        if (statusRow is null && months.Count == 0) return null;

        // Missing status → gaps = [], completeMonths = null, no listing clamp.
        DataGap[] gaps = [];
        string[]? completeMonths = null;
        if (statusRow is not null)
        {
            gaps = JsonSerializer.Deserialize<DataGap[]>(statusRow.GapsJson, ManifestJson.Options) ?? [];
            completeMonths = JsonSerializer.Deserialize<string[]>(statusRow.CompleteMonthsJson, ManifestJson.Options);
        }

        var coveredMonths = new List<string>();
        foreach (var row in months)
        {
            var year  = int.Parse(row.Month[..4], NumberStyles.None, CultureInfo.InvariantCulture);
            var month = int.Parse(row.Month[5..], NumberStyles.None, CultureInfo.InvariantCulture);
            // Pass first-data timestamp only when it falls inside this month so the listing-month
            // coverage check agrees with the backfill planner (pre-listing hole is unrecordable).
            var listingClamp = MonthCoverageMath.ListingClamp(statusRow?.FirstTs, year, month);
            if (MonthCoverageMath.IsCovered(feedName, interval, year, month, row.Rows, gaps, completeMonths, listingClamp, nowMs))
                coveredMonths.Add(row.Month);
        }

        coveredMonths.Sort(StringComparer.Ordinal);

        object entry = new
        {
            feed_name = feedName,
            interval,
            covered_months = coveredMonths.ToArray(),
            first_timestamp = statusRow?.FirstTs,
            last_timestamp = statusRow?.LastTs,
        };
        return (entry, coveredMonths);
    }
}
