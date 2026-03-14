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
        group.MapPost("/circuit-breaker/reset", ResetCircuitBreaker);
        return group;
    }

    private static IResult GetAllStatus(
        IOptionsMonitor<HistoryLoaderOptions> options,
        IFeedStatusStore feedStatusStore)
    {
        var config = options.CurrentValue;
        var symbols = new List<SymbolStatus>();

        foreach (var asset in config.Assets)
        {
            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
            var feedSummaries = new List<FeedStatusSummary>();

            foreach (var feed in asset.Feeds)
            {
                var status = feedStatusStore.Load(assetDir, feed.Name, feed.Interval);
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
        IOptionsMonitor<HistoryLoaderOptions> options,
        IFeedStatusStore feedStatusStore)
    {
        var config = options.CurrentValue;

        var asset = config.Assets.FirstOrDefault(a =>
        {
            var dirName = AssetPathConvention.DirectoryName(a.Symbol, a.Type);
            return string.Equals(dirName, symbol, StringComparison.OrdinalIgnoreCase);
        });

        if (asset is null)
            return Results.NotFound(new { error = "Symbol not found", symbol });

        var resolvedAssetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
        var feedDetails = new List<FeedStatusDetail>();

        foreach (var feed in asset.Feeds)
        {
            var status = feedStatusStore.Load(resolvedAssetDir, feed.Name, feed.Interval);
            if (status is not null)
            {
                feedDetails.Add(new FeedStatusDetail(
                    FeedName: status.FeedName,
                    Interval: status.Interval,
                    FirstTimestamp: status.FirstTimestamp,
                    LastTimestamp: status.LastTimestamp,
                    LastRunUtc: status.LastRunUtc,
                    RecordCount: status.RecordCount,
                    GapCount: status.Gaps.Count,
                    Health: status.Health.ToString()));
            }
        }

        return Results.Json(new SymbolDetailResponse(
            Symbol: asset.Symbol,
            Type: asset.Type,
            Exchange: asset.Exchange,
            Feeds: feedDetails));
    }

    private static IResult ResetCircuitBreaker(ICollectionCircuitBreaker circuitBreaker)
    {
        circuitBreaker.Reset();
        return Results.Ok(new { message = "Circuit breaker reset" });
    }
}
