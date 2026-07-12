using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class StatusEndpoints
{
    public static RouteGroupBuilder MapStatusEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/status");
        group.MapGet("/", GetAllStatus);
        group.MapGet("/{symbol}", GetSymbolStatus);
        group.MapGet("/circuit-breaker", GetCircuitBreakerStatus);
        group.MapPost("/circuit-breaker/reset", ResetCircuitBreaker);
        return group;
    }

    private static async Task<IResult> GetAllStatus(
        IOptionsMonitor<HistoryLoaderOptions> options,
        ICollectionPlanSource planSource,
        IFeedStatusStore feedStatusStore,
        CancellationToken ct)
    {
        var dataRoot = options.CurrentValue.DataRoot;
        var symbols = new List<SymbolStatus>();

        foreach (var asset in planSource.Current.Assets)
        {
            var assetDir = BackfillOrchestrator.ResolveAssetDir(dataRoot, asset);
            var feedSummaries = new List<FeedStatusSummary>();

            foreach (var feed in asset.Feeds)
            {
                var status = await feedStatusStore.Load(assetDir, feed.FeedName, feed.Interval, ct);
                var health = status?.Health.ToString() ?? "Unknown";
                var gapCount = status?.Gaps.Count ?? 0;

                feedSummaries.Add(new FeedStatusSummary(
                    Name: feed.FeedName,
                    Interval: feed.Interval,
                    LastTimestamp: status?.LastTimestamp,
                    GapCount: gapCount,
                    Health: health));
            }

            symbols.Add(new SymbolStatus(
                Symbol: asset.Venue.ApiSymbol,
                Type: asset.Venue.AssetType,
                Exchange: asset.Exchange,
                FeedCount: asset.Feeds.Count,
                Feeds: feedSummaries));
        }

        return Results.Json(new StatusResponse(symbols));
    }

    private static async Task<IResult> GetSymbolStatus(
        string symbol,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ICollectionPlanSource planSource,
        IFeedStatusStore feedStatusStore,
        BackfillOrchestrator orchestrator,
        CancellationToken ct)
    {
        var asset = planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Venue.Dir, symbol, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
            return TypedResults.Json(new ErrorBody("symbol_not_found", $"symbol '{symbol}' not found"),
                statusCode: StatusCodes.Status404NotFound);

        var resolvedAssetDir = BackfillOrchestrator.ResolveAssetDir(options.CurrentValue.DataRoot, asset);
        var feedDetails = new List<FeedStatusDetail>();

        foreach (var feed in asset.Feeds)
        {
            var status = await feedStatusStore.Load(resolvedAssetDir, feed.FeedName, feed.Interval, ct);
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
            Symbol: asset.Venue.ApiSymbol,
            Type: asset.Venue.AssetType,
            Exchange: asset.Exchange,
            BackfillRunning: orchestrator.IsRunning(resolvedAssetDir),
            Feeds: feedDetails));
    }

    private static IResult GetCircuitBreakerStatus(ICollectionCircuitBreaker circuitBreaker) =>
        Results.Json(new
        {
            isTripped = circuitBreaker.IsTripped,
            reason = circuitBreaker.Reason?.ToString(),
            isAutoResettable = circuitBreaker.IsAutoResettable
        });

    private static IResult ResetCircuitBreaker(ICollectionCircuitBreaker circuitBreaker)
    {
        circuitBreaker.Reset();
        return Results.Ok(new { message = "Circuit breaker reset" });
    }
}
