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
    ILoadAssetResolver assetResolver,
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

            await RunJobAsync(job, stoppingToken);
        }
    }

    private async Task RunJobAsync(LoadJob job, CancellationToken ct)
    {
        registry.OnStarted(job.JobId);
        try
        {
            var asset = await assetResolver.Resolve(job.Exchange, job.Symbol, job.AssetType, ct);

            // Append a transient feed entry if the asset config doesn't already carry one
            // for this exact name+interval. This is never persisted to appsettings.
            var hasEntry = asset.Feeds.Any(f => f.Name == job.FeedName && f.Interval == job.Interval);
            if (!hasEntry)
                asset.Feeds.Add(new FeedCollectionConfig { Name = job.FeedName, Interval = job.Interval });

            var assetDir = BackfillOrchestrator.ResolveAssetDir(options.CurrentValue.DataRoot, asset);
            var ok = await orchestrator.TryRunSingleAsync(
                asset, assetDir, feedFilter: [job.FeedName], fromDate: job.From, toDate: job.To, ct);

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
