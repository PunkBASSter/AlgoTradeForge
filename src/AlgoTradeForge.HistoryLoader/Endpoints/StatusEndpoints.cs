using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Endpoints;

internal static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatusEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/status");
        group.MapGet("/", GetAllStatus);
        group.MapGet("/{symbol}", GetSymbolStatus);
        return group;
    }

    private static IResult GetAllStatus(
        IOptions<HistoryLoaderOptions> options,
        IFeedStatusStore feedStatusStore)
    {
        var config = options.Value;
        var symbols = new List<SymbolStatus>();

        foreach (var asset in config.Assets)
        {
            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
            var feedSummaries = new List<FeedStatusSummary>();

            foreach (var feed in asset.Feeds)
            {
                var status = feedStatusStore.Load(assetDir, feed.Name);
                var health = status?.Health.ToString() ?? "Unknown";
                var gapCount = status?.Gaps.Count ?? 0;

                feedSummaries.Add(new FeedStatusSummary(
                    Name: feed.Name,
                    Interval: feed.Interval,
                    LastTimestamp: status?.LastTimestamp,
                    GapCount: gapCount,
                    Health: health));
            }

            symbols.Add(new SymbolStatus(
                Symbol: asset.Symbol,
                Type: asset.Type,
                Exchange: asset.Exchange,
                FeedCount: asset.Feeds.Count,
                Feeds: feedSummaries));
        }

        return Results.Json(new StatusResponse(symbols));
    }

    private static IResult GetSymbolStatus(
        string symbol,
        IOptions<HistoryLoaderOptions> options,
        IFeedStatusStore feedStatusStore)
    {
        var config = options.Value;

        var asset = config.Assets.FirstOrDefault(a =>
        {
            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, a);
            var dirName = Path.GetFileName(assetDir);
            return string.Equals(dirName, symbol, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase);
        });

        if (asset is null)
            return Results.NotFound(new { error = "Symbol not found", symbol });

        var resolvedAssetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
        var feedStatuses = new List<FeedStatus>();

        foreach (var feed in asset.Feeds)
        {
            var status = feedStatusStore.Load(resolvedAssetDir, feed.Name);
            if (status is not null)
                feedStatuses.Add(status);
        }

        return Results.Json(new SymbolDetailResponse(
            Symbol: asset.Symbol,
            Type: asset.Type,
            Exchange: asset.Exchange,
            Feeds: feedStatuses));
    }
}
