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

                // Index row first: crash after index delete leaves file-present + index-absent → self-heals on rescan.
                // Crash after File.Delete leaves index-absent + file-absent → MaterializeMonth re-runs automatically.
                // Reverse order (file gone + stale index row) would read as covered and silently lose the partition.
                await index.DeleteMonthPartition(exchange, body.Dir, feed.FeedName, feed.Interval, month, ct);
                File.Delete(file);
                await materializer.MaterializeMonth(asset, feed, assetDir, year, mon, ct);
                months.Add(month);
            }

            if (months.Count == 0)
                continue;

            // Authoritative RecordCount: the accumulator cannot self-correct from an inflated base.
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(feedDir, $"????-??_{feed.Interval}.csv"))
                total += (await PartitionAudit.Count(file, ct)).Distinct;

            // Authoritative RecordCount under the per-path lock: Update always writes (idempotent when
            // unchanged), so no read-before-write guard — the accumulator cannot self-correct from an
            // inflated base, and a concurrent collector cycle can no longer clobber this recompute.
            // FeedStatus is a sealed class (not a record) — copy explicitly, override RecordCount.
            await statusStore.Update(assetDir, feed.FeedName, feed.Interval, existing => existing is null
                ? new FeedStatus { FeedName = feed.FeedName, Interval = feed.Interval, RecordCount = total }
                : new FeedStatus
                {
                    FeedName = existing.FeedName,
                    Interval = existing.Interval,
                    FirstTimestamp = existing.FirstTimestamp,
                    LastTimestamp = existing.LastTimestamp,
                    LastRunUtc = existing.LastRunUtc,
                    RecordCount = total,
                    Gaps = existing.Gaps,
                    Health = existing.Health,
                    CompleteMonths = existing.CompleteMonths,
                }, ct);
            log.LogInformation("Dedup {Feed}/{Interval} {Dir}: RecordCount -> {New}",
                feed.FeedName, feed.Interval, body.Dir, total);

            repaired.Add(new { feed = feed.FeedName, interval = feed.Interval, months });
        }

        return Results.Ok(new { exchange = body.Exchange, dir = body.Dir, repaired });
    }
}
