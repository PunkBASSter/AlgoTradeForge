using Microsoft.Extensions.Options;

namespace AlgoTradeForge.CandleIngestor;

public sealed class IngestionWorker(
    IngestionOrchestrator orchestrator,
    IOptions<CandleIngestorOptions> options,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = options.Value;

        if (config.RunOnStartup)
        {
            logger.LogInformation("RunOnStartup enabled, executing initial ingestion");
            await RunSafeAsync(stoppingToken);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(config.ScheduleIntervalHours));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSafeAsync(stoppingToken);
        }
    }

    private async Task RunSafeAsync(CancellationToken ct)
    {
        try
        {
            await orchestrator.RunAsync(ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Ingestion cancelled due to shutdown");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingestion run failed, will retry on next schedule");
        }
    }
}
