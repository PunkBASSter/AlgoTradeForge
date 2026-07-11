using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Application.Archive.Jobs;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

internal sealed class LoadJobWorker(
    ILoadJobRegistry registry,
    BackfillOrchestrator orchestrator,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<LoadJobWorker> logger) : BackgroundService
{
    // Canonical three-part form (FundingInfoRefreshService / ScheduledCollectorService).
    private static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException oce
        && stoppingToken.IsCancellationRequested
        && oce.CancellationToken == stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var job = await registry.Dequeue(stoppingToken);
            if (job is null)
                return;

            await RunJob(job, stoppingToken);
        }
    }

    private async Task RunJob(LoadJob job, CancellationToken ct)
    {
        registry.OnStarted(job.JobId);
        try
        {
            // Append a transient feed entry if the asset doesn't already carry one for
            // this exact name+interval. Clone first — never mutate the shared plan asset.
            var asset = job.Asset;
            var hasEntry = asset.Feeds.Any(f => f.FeedName == job.FeedName && f.Interval == job.Interval);
            if (!hasEntry)
                asset = asset with
                {
                    Feeds = [..asset.Feeds, new CollectionFeed(job.FeedName, job.Interval, "on-demand", "csv", job.From)],
                };

            var assetDir = BackfillOrchestrator.ResolveAssetDir(options.CurrentValue.DataRoot, asset);
            var ok = await orchestrator.TryRunSingle(
                asset, assetDir, feedFilter: [job.FeedName], fromDate: job.From, toDate: job.To,
                progress: new LoadJobProgress(registry, job.JobId), ct: ct);

            if (ok)
                registry.OnCompleted(job.JobId);
            else
                registry.OnErrored(job.JobId, "symbol_busy", "Another backfill holds the symbol lock; retry later.");
        }
        catch (ArchiveIntegrityException ex)
        {
            registry.OnErrored(job.JobId, "checksum_mismatch", ex.Message);
        }
        catch (Exception ex) when (!IsTrueShutdown(ex, ct))
        {
            logger.LogError(ex, "Load job {JobId} failed", job.JobId);
            registry.OnErrored(job.JobId, "load_failed", ex.Message);
        }
    }
}
