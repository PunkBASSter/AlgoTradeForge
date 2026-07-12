using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

internal sealed class LoadJobWorker(
    [FromKeyedServices("load")] IJobWakeupQueue wakeup,
    IHistoryIndex index,
    IArchiveLoadService archiveLoad,
    IJobProgressSinkFactory sinkFactory,
    IJobCancellationMap cancellations,
    LoadRequestRehydrator rehydrator,
    ILogger<LoadJobWorker> logger) : BackgroundService
{
    // Canonical three-part form (FundingInfoRefreshService / ScheduledCollectorService).
    private static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException oce
        && stoppingToken.IsCancellationRequested
        && oce.CancellationToken == stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Re-arm from the durable store: rows still 'queued' after a restart never got a wakeup.
        var queued = await index.ListJobs("load", "queued", stoppingToken);
        wakeup.SeedFromQueued(queued.Select(j => j.Id));

        await foreach (var jobId in wakeup.Reader(stoppingToken))
            await RunJob(jobId, stoppingToken);
    }

    // Processes exactly one wakeup item then returns — unit-test seam.
    internal async Task DrainOnceForTest(CancellationToken ct)
    {
        await foreach (var jobId in wakeup.Reader(ct))
        {
            await RunJob(jobId, ct);
            return;
        }
    }

    private async Task RunJob(string jobId, CancellationToken stoppingToken)
    {
        var row = await index.GetJob(jobId, stoppingToken);
        if (row is null)
        {
            logger.LogWarning("Load job {JobId} woke the worker but is no longer in the store", jobId);
            return;
        }

        var linked = cancellations.Register(jobId, stoppingToken);
        try
        {
            await index.UpdateJob(jobId, "running", ct: stoppingToken);
            var sink = sinkFactory.For(jobId);
            // Run owns the terminal sink transitions (Started/Complete/Fail).
            var req = rehydrator.Rehydrate(row);
            await archiveLoad.Run(req, sink, linked);
        }
        catch (Exception ex) when (!IsTrueShutdown(ex, stoppingToken))
        {
            // Reaches here only for pre-Run failures (e.g. rehydration): Run swallows its own.
            logger.LogError(ex, "Load job {JobId} failed before dispatch", jobId);
            try { await sinkFactory.For(jobId).Fail("load_failed", ex.Message, stoppingToken); }
            catch (Exception inner) when (!IsTrueShutdown(inner, stoppingToken))
            {
                logger.LogError(inner, "Failed to record load job {JobId} failure", jobId);
            }
        }
        finally
        {
            cancellations.Remove(jobId);
        }
    }
}
