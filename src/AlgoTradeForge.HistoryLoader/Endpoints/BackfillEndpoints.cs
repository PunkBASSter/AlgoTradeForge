using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
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
        IOptionsMonitor<HistoryLoaderOptions> options,
        BackfillOrchestrator orchestrator,
        IHostApplicationLifetime lifetime,
        ILoggerFactory loggerFactory)
    {
        var config = options.CurrentValue;
        var symbol = request.Symbol;

        if (string.IsNullOrWhiteSpace(symbol))
            return Results.BadRequest(new { error = "Symbol is required" });

        var asset = config.Assets.FirstOrDefault(a =>
        {
            var dirName = AssetPathConvention.DirectoryName(a.Symbol, a.Type);
            return string.Equals(dirName, symbol, StringComparison.OrdinalIgnoreCase);
        });

        if (asset is null)
            return Results.BadRequest(new { error = "Symbol not configured", symbol });

        var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);
        var feedFilter = request.Feeds is { Length: > 0 } ? (IReadOnlyList<string>)request.Feeds : null;
        var fromDate = request.FromDate;
        var ct = lifetime.ApplicationStopping;
        var logger = loggerFactory.CreateLogger("BackfillEndpoints");

        _ = Task.Run(async () =>
        {
            try
            {
                if (!await orchestrator.TryRunSingleAsync(asset, assetDir, feedFilter, fromDate, ct))
                    logger.LogWarning("Backfill already running for {Symbol}", asset.Symbol);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Backfill failed for {Symbol}", asset.Symbol);
            }
        }, ct);

        var feedsQueued = feedFilter?.ToArray()
            ?? asset.Feeds.Where(f => f.Enabled).Select(f => f.Name).ToArray();

        return Results.Accepted(value: new BackfillResponse(
            Symbol: asset.Symbol,
            FeedsQueued: feedsQueued,
            Message: $"Backfill queued for {asset.Symbol}"));
    }
}
