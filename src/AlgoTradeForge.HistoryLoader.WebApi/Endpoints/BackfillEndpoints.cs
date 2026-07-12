using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class BackfillEndpoints
{
    public static RouteGroupBuilder MapBackfillEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1");
        group.MapPost("/backfill", TriggerBackfill);
        return group;
    }

    private static IResult TriggerBackfill(
        BackfillRequest request,
        IOptionsMonitor<HistoryLoaderOptions> options,
        BackfillOrchestrator orchestrator,
        ICollectionPlanSource planSource,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        var symbol = request.Symbol;

        if (string.IsNullOrWhiteSpace(symbol))
            return Results.BadRequest(new { error = "Symbol is required" });

        var asset = planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Venue.Dir, symbol, StringComparison.OrdinalIgnoreCase));

        if (asset is null)
            return Results.BadRequest(new { error = "Symbol not configured", symbol });

        var assetDir = BackfillOrchestrator.ResolveAssetDir(options.CurrentValue.DataRoot, asset);

        if (orchestrator.IsRunning(assetDir))
            return Results.Conflict(new { error = "Backfill already running", symbol = asset.Venue.ApiSymbol });

        var feedFilter = request.Feeds is { Length: > 0 } ? (IReadOnlyList<string>)request.Feeds : null;
        var fromDate = request.FromDate;
        var ct = lifetime.ApplicationStopping;
        var logger = loggerFactory.CreateLogger("BackfillEndpoints");

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await orchestrator.TryRunSingle(asset, assetDir, feedFilter, fromDate, ct: ct))
                    logger.LogWarning("Backfill already running for {Symbol}", asset.Venue.ApiSymbol);
            }
            catch (Exception ex) when (
                !(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                // HttpClient timeouts surface as TaskCanceledException (an OCE); without the
                // ct.IsCancellationRequested qualifier the unobserved-task crash would kill the host.
                logger.LogError(ex, "Backfill failed for {Symbol}", asset.Venue.ApiSymbol);
            }
        }, ct);

        var feedsQueued = feedFilter?.ToArray()
            ?? asset.Feeds.Select(f => f.FeedName).ToArray();

        return Results.Accepted(value: new BackfillResponse(
            Symbol: asset.Venue.ApiSymbol,
            FeedsQueued: feedsQueued,
            Message: $"Backfill queued for {asset.Venue.ApiSymbol}"));
    }
}
