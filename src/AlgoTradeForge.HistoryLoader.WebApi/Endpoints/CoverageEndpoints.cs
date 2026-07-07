using System.Globalization;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
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
        ISchemaManager schemaManager,
        IFeedStatusStore feedStatusStore,
        IMonthCoverageCalculator coverageCalculator,
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
        var assetDir = Path.Combine(opts.DataRoot, exchange,
            AssetPathConvention.DirectoryName(symbol, assetType));

        var manifest = await schemaManager.Load(assetDir, ct);
        if (manifest is null)
            return Results.Ok(new { asset_dir = assetDir, feeds = Array.Empty<object>() });

        var feedEntries = new List<object>();

        // Candle intervals from Candles.Intervals
        foreach (var interval in manifest.Candles?.Intervals ?? [])
        {
            var entry = await BuildFeedEntry(
                assetDir, FeedNames.Candles, interval,
                feedStatusStore, coverageCalculator, ct);
            if (entry is not null) feedEntries.Add(entry);
        }

        // Declared feeds with an interval (skip alt-bar/tick/side entries without intervals)
        foreach (var (feedName, def) in manifest.Feeds)
        {
            if (string.IsNullOrEmpty(def.Interval)) continue;

            var entry = await BuildFeedEntry(
                assetDir, feedName, def.Interval,
                feedStatusStore, coverageCalculator, ct);
            if (entry is not null) feedEntries.Add(entry);
        }

        return Results.Ok(new { asset_dir = assetDir, feeds = feedEntries });
    }

    private static async Task<object?> BuildFeedEntry(
        string assetDir, string feedName, string interval,
        IFeedStatusStore feedStatusStore, IMonthCoverageCalculator coverageCalculator,
        CancellationToken ct)
    {
        var feedDir = Path.Combine(assetDir, feedName);
        if (!Directory.Exists(feedDir)) return null;

        var status = await feedStatusStore.Load(assetDir, feedName, interval, ct);
        var gaps = status?.Gaps ?? [];

        var coveredMonths = new List<string>();
        foreach (var file in Directory.EnumerateFiles(feedDir, $"????-??_{interval}.csv"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            // fileName = "2024-01_1h"; split on '_' to get "2024-01"
            var underscoreIdx = fileName.IndexOf('_');
            if (underscoreIdx < 7) continue;
            var monthPart = fileName[..underscoreIdx]; // "yyyy-MM"
            if (!int.TryParse(monthPart[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)) continue;
            if (!int.TryParse(monthPart[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var month)) continue;

            // Pass first-data timestamp only when it falls inside this month so the listing-month
            // coverage check agrees with the backfill planner (pre-listing hole is unrecordable).
            long? listingClamp = null;
            if (status?.FirstTimestamp is { } firstMs)
            {
                var mStartMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                var mEndMs = month == 12
                    ? new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()
                    : new DateTimeOffset(year, month + 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
                if (firstMs >= mStartMs && firstMs < mEndMs)
                    listingClamp = firstMs;
            }
            if (await coverageCalculator.IsMonthCovered(assetDir, feedName, interval, year, month, gaps, listingClamp, ct))
                coveredMonths.Add(monthPart);
        }

        coveredMonths.Sort(StringComparer.Ordinal);

        return new
        {
            feed_name = feedName,
            interval,
            covered_months = coveredMonths.ToArray(),
            first_timestamp = status?.FirstTimestamp,
            last_timestamp = status?.LastTimestamp,
        };
    }
}
