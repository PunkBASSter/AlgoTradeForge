using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Index;

namespace AlgoTradeForge.HistoryLoader.WebApi.Index;

/// <summary>
/// Single consumer of the index work queue — serializes all SQLite writes. Subscribes to
/// ManifestChanged so manifest mutations index without polling; triggers an initial rebuild
/// when the index is empty (first boot or deleted DB).
/// </summary>
internal sealed class IndexMaintenanceService(
    IndexMaintenanceQueue queue,
    IndexWorkProcessor processor,
    HistoryIndexInitializer initializer,
    IHistoryIndex index,
    ISchemaManager schemaManager,
    ILogger<IndexMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await initializer.EnsureCreated(stoppingToken);

        schemaManager.ManifestChanged += assetDir =>
            queue.Enqueue(new IndexWork.ManifestTouched(assetDir));

        // Bootstrap: empty index (first boot / deleted DB) OR a rebuild that died mid-crawl.
        // IsEmpty alone is not enough — a 12k-asset crawl interrupted halfway leaves a non-empty
        // index with half the catalog silently missing (initializer marked that job 'interrupted').
        var lastRebuild = await index.GetLastJob("rebuild", stoppingToken);
        if (await index.IsEmpty(stoppingToken) || lastRebuild?.State == "interrupted")
        {
            var jobId = await index.CreateJob("rebuild", stoppingToken);
            queue.Enqueue(new IndexWork.Rebuild(jobId));
            logger.LogInformation("Index bootstrap rebuild queued as job {JobId} (empty={Empty}, lastState={LastState})",
                jobId, await index.IsEmpty(stoppingToken), lastRebuild?.State);
        }

        await foreach (var work in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await processor.Process(work, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Index maintenance failed for {Work}", work);
            }
        }
    }
}
