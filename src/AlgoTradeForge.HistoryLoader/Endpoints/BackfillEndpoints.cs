using AlgoTradeForge.HistoryLoader.Collection;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Endpoints;

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
        IOptions<HistoryLoaderOptions> options,
        BackfillOrchestrator orchestrator)
    {
        var config = options.Value;
        var symbol = request.Symbol;

        var asset = config.Assets.FirstOrDefault(a =>
        {
            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, a);
            var dirName = Path.GetFileName(assetDir);
            return string.Equals(dirName, symbol, StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.Symbol, symbol, StringComparison.OrdinalIgnoreCase);
        });

        if (asset is null)
            return Results.BadRequest(new { error = "Symbol not configured", symbol });

        var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);

        if (orchestrator.IsRunning(assetDir))
            return Results.Conflict(new { error = "Backfill already running for this symbol", symbol });

        var feedFilter = request.Feeds is { Length: > 0 } ? (IReadOnlyList<string>)request.Feeds : null;
        var fromDate = request.FromDate;

        _ = Task.Run(() => orchestrator.RunSingleAsync(asset, assetDir, feedFilter, fromDate));

        var feedsQueued = feedFilter?.ToArray()
            ?? asset.Feeds.Where(f => f.Enabled).Select(f => f.Name).ToArray();

        return Results.Accepted(value: new BackfillResponse(
            Symbol: asset.Symbol,
            FeedsQueued: feedsQueued,
            Message: $"Backfill queued for {asset.Symbol}"));
    }
}
