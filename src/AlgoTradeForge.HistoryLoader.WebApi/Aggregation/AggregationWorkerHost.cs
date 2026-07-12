using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;

namespace AlgoTradeForge.HistoryLoader.WebApi.Aggregation;

/// <summary>
/// Two store-backed worker pools that drain the aggregation wakeup queues. The time-bar and
/// tick pools are separate keyed <see cref="IJobWakeupQueue"/> doorbells so I/O-heavy tick jobs
/// don't head-of-line CPU-heavy time-bar jobs. The durable row is the source of truth; a wakeup
/// only hints that a queued row exists. Mirrors <c>LoadJobWorker</c>'s drain / per-item isolation
/// / cancellation-classification structure per pool.
/// </summary>
internal sealed class AggregationWorkerHost(
    [FromKeyedServices("aggregation-timebar")] IJobWakeupQueue timeBarWakeup,
    [FromKeyedServices("aggregation-tick")] IJobWakeupQueue tickWakeup,
    IHistoryIndex index,
    IAggregationService aggregationService,
    IJobProgressSinkFactory sinkFactory,
    IJobCancellationMap cancellations,
    AggregationRequestRehydrator rehydrator,
    ILogger<AggregationWorkerHost> logger) : BackgroundService
{
    // Canonical three-part form (matches LoadJobWorker / ScheduledCollectorService).
    private static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException oce
        && stoppingToken.IsCancellationRequested
        && oce.CancellationToken == stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedFromQueued(stoppingToken);

        // One serial drain loop per pool. The wakeup channel is SingleReader, so each pool has a
        // single consumer; the split preserves head-of-line isolation between tick and time-bar.
        await Task.WhenAll(
            Drain(timeBarWakeup, maxItems: null, stoppingToken),
            Drain(tickWakeup, maxItems: null, stoppingToken));
    }

    // Re-arm both pools from the durable store: rows still 'queued' after a restart never got a
    // wakeup. Route each to the pool matching its source type; a row we can't rehydrate (e.g. a
    // removed symbol) still gets queued to the time-bar pool so RunJob fails it cleanly.
    private async Task SeedFromQueued(CancellationToken stoppingToken)
    {
        var queued = await index.ListJobs("aggregation", "queued", stoppingToken);
        foreach (var row in queued)
            PoolFor(row).TryEnqueue(row.Id);
    }

    private IJobWakeupQueue PoolFor(IndexJobRow row)
    {
        try
        {
            return rehydrator.Rehydrate(row).Source.Kind == DataFeedKind.Tick ? tickWakeup : timeBarWakeup;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not determine source pool for queued aggregation job {JobId}; routing to time-bar pool", row.Id);
            return timeBarWakeup;
        }
    }

    // Processes exactly one wakeup item off the time-bar pool then returns — unit-test seam.
    internal Task DrainOnceForTest(CancellationToken ct) => Drain(timeBarWakeup, maxItems: 1, ct);

    // Processes up to `count` wakeup items off the time-bar pool then returns — multi-item test seam.
    internal Task DrainForTest(int count, CancellationToken ct) => Drain(timeBarWakeup, count, ct);

    // Same, off the tick pool — exercises tick-pool dispatch.
    internal Task DrainTickOnceForTest(CancellationToken ct) => Drain(tickWakeup, maxItems: 1, ct);

    // Shared drain loop. Per-item faults are isolated inside RunJob so one bad row never kills the
    // loop; the outer catch swallows host-shutdown (including the rethrow from RunJob's shutdown arm)
    // so the BackgroundService exits cleanly instead of faulting → StopHost → all collectors down.
    private async Task Drain(IJobWakeupQueue wakeup, int? maxItems, CancellationToken stoppingToken)
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
            logger.LogError(ex, "Failed to read aggregation job {JobId}; skipping wakeup", jobId);
            return;
        }

        if (row is null)
        {
            logger.LogWarning("Aggregation job {JobId} woke the worker but is no longer in the store", jobId);
            return;
        }

        var sink = sinkFactory.For(jobId);
        try
        {
            var linked = cancellations.Register(jobId, stoppingToken);
            await index.UpdateJob(jobId, "running", ct: stoppingToken);
            // Run owns the terminal sink transitions on the happy path (Started/Complete) and reports
            // its own aggregation errors via Fail; it lets OCE propagate on any cancel so the HOST
            // classifies user-cancel vs host-shutdown (D2: host owns cancellation ownership).
            var job = rehydrator.Rehydrate(row);
            await aggregationService.Run(new AggregationRunRequest(job), sink, linked);
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
            // Pre-Run failures (rehydration of a removed symbol, UpdateJob) and any genuine escape.
            logger.LogError(ex, "Aggregation job {JobId} failed", jobId);
            await sink.Fail("aggregation_failed", ex.Message, CancellationToken.None);
        }
        finally
        {
            cancellations.Remove(jobId);
        }
    }
}
