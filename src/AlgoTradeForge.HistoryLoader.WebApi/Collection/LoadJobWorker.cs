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

        await Drain(maxItems: null, stoppingToken);
    }

    // Processes exactly one wakeup item then returns — unit-test seam.
    internal Task DrainOnceForTest(CancellationToken ct) => Drain(maxItems: 1, ct);

    // Processes up to `count` wakeup items then returns — multi-item unit-test seam.
    internal Task DrainForTest(int count, CancellationToken ct) => Drain(count, ct);

    // Shared drain loop. Per-item faults are isolated inside RunJob so one bad row never kills the
    // loop; the outer catch swallows host-shutdown (including the rethrow from RunJob's shutdown arm)
    // so the BackgroundService exits cleanly instead of faulting → StopHost → all collectors down.
    private async Task Drain(int? maxItems, CancellationToken stoppingToken)
    {
        var processed = 0;
        try
        {
            await foreach (var jobId in wakeup.Reader(stoppingToken))
            {
                await RunJob(jobId, stoppingToken);
                if (maxItems is { } max && ++processed >= max)
                    return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task RunJob(string jobId, CancellationToken stoppingToken)
    {
        IndexJobRow? row;
        try
        {
            row = await index.GetJob(jobId, stoppingToken);
        }
        catch (Exception ex) when (!IsTrueShutdown(ex, stoppingToken))
        {
            // Transient store read (e.g. SQLITE_BUSY): we have no row to Fail — skip and keep draining.
            logger.LogError(ex, "Failed to read load job {JobId}; skipping wakeup", jobId);
            return;
        }

        if (row is null)
        {
            logger.LogWarning("Load job {JobId} woke the worker but is no longer in the store", jobId);
            return;
        }

        var sink = sinkFactory.For(jobId);
        try
        {
            var linked = cancellations.Register(jobId, stoppingToken);
            await index.UpdateJob(jobId, "running", ct: stoppingToken);
            // Run owns the terminal sink transitions on the happy path (Started/Complete) and reports
            // its own load errors via Fail; it rethrows OCE carrying the linked token on any cancel.
            var req = rehydrator.Rehydrate(row);
            await archiveLoad.Run(req, sink, linked);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // User DELETE tripped the linked (per-job) token; the host is still running.
            await sink.Cancel("user_cancelled", CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown mid-run — leave the row non-terminal for restart rehydration (M3.6 reconciles).
            throw;
        }
        catch (Exception ex)
        {
            // Pre-Run failures (rehydration of a removed symbol) and any genuine escape.
            logger.LogError(ex, "Load job {JobId} failed", jobId);
            await sink.Fail("load_failed", ex.Message, CancellationToken.None);
        }
        finally
        {
            cancellations.Remove(jobId);
        }
    }
}
