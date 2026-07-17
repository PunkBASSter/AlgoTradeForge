using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Endpoints;

internal static class MaintenanceEndpoints
{
    private static readonly string[] MetricsFeeds =
    [
        FeedNames.OpenInterest, FeedNames.LsRatioGlobal,
        FeedNames.LsRatioTopAccounts, FeedNames.LsRatioTopPositions,
    ];

    public sealed record DedupRequest(string Exchange, string Dir);

    public static WebApplication MapMaintenanceEndpoints(this WebApplication app)
    {
        app.MapGroup("/api/v1").MapPost("/maintenance/dedup-partitions", Dedup);
        return app;
    }

    internal static async Task<IResult> Dedup(
        DedupRequest body,
        ICollectionPlanSource planSource,
        ArchiveMaterializerRegistry registry,
        IHistoryIndex index,
        IFeedStatusStore statusStore,
        IOptionsMonitor<HistoryLoaderOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var exchange = body.Exchange.ToLowerInvariant();
        var asset = planSource.Current.Assets.FirstOrDefault(a =>
            string.Equals(a.Exchange, exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Venue.Dir, body.Dir, StringComparison.Ordinal));
        if (asset is null)
            return Results.NotFound(new { error = "asset not declared in any enabled group", body.Exchange, body.Dir });

        var log = loggerFactory.CreateLogger("MaintenanceDedup");
        var assetDir = BackfillOrchestrator.ResolveAssetDir(options.CurrentValue.DataRoot, asset);
        var repaired = new List<object>();

        foreach (var feed in asset.Feeds.Where(f => MetricsFeeds.Contains(f.FeedName)))
        {
            var materializer = registry.Resolve(exchange, feed.FeedName, asset.Venue.AssetType);
            if (materializer is null || string.IsNullOrEmpty(feed.Interval))
                continue;

            var feedDir = Path.Combine(assetDir, feed.FeedName);
            if (!Directory.Exists(feedDir))
                continue;

            var months = new List<string>();
            foreach (var file in Directory.EnumerateFiles(feedDir, $"????-??_{feed.Interval}.csv"))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length < 7 || name[4] != '-') continue;
                var month = name[..7]; // "yyyy-MM"

                var (lines, distinct) = await PartitionAudit.Count(file, ct);
                if (lines <= distinct)
                    continue; // clean — idempotent skip

                var year = int.Parse(month[..4]);
                var mon = int.Parse(month[5..7]);

                // Delete first: the replace-guard refuses to overwrite a fuller (doubled) file.
                File.Delete(file);
                await index.DeleteMonthPartition(exchange, body.Dir, feed.FeedName, feed.Interval, month, ct);
                await materializer.MaterializeMonth(asset, feed, assetDir, year, mon, ct);
                months.Add(month);
            }

            if (months.Count == 0)
                continue;

            // Authoritative RecordCount: the accumulator cannot self-correct from an inflated base.
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(feedDir, $"????-??_{feed.Interval}.csv"))
                total += (await PartitionAudit.Count(file, ct)).Distinct;

            var status = await statusStore.Load(assetDir, feed.FeedName, feed.Interval, ct);
            if (status is not null && status.RecordCount != total)
            {
                // FeedStatus is a sealed class (not a record) — copy explicitly, override RecordCount.
                await statusStore.Save(assetDir, feed.FeedName, feed.Interval, new FeedStatus
                {
                    FeedName = status.FeedName,
                    Interval = status.Interval,
                    FirstTimestamp = status.FirstTimestamp,
                    LastTimestamp = status.LastTimestamp,
                    LastRunUtc = status.LastRunUtc,
                    RecordCount = total,
                    Gaps = status.Gaps,
                    Health = status.Health,
                    CompleteMonths = status.CompleteMonths,
                }, ct);
                log.LogInformation("Dedup {Feed}/{Interval} {Dir}: RecordCount {Old} -> {New}",
                    feed.FeedName, feed.Interval, body.Dir, status.RecordCount, total);
            }

            repaired.Add(new { feed = feed.FeedName, interval = feed.Interval, months });
        }

        return Results.Ok(new { exchange = body.Exchange, dir = body.Dir, repaired });
    }
}
