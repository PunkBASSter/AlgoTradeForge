using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Index;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Jobs;

internal sealed class JobRetentionSweeper(
    IHistoryIndex index,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<JobRetentionSweeper> logger) : BackgroundService
{
    // HttpClient timeouts surface as OperationCanceledException too — only the stoppingToken's
    // own cancellation is a true shutdown; anything else (e.g. SQLITE_BUSY timeout) logs and continues.
    private static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException oce
        && stoppingToken.IsCancellationRequested
        && oce.CancellationToken == stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var jobs = options.CurrentValue.Jobs;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(jobs.RetentionSweepMinutes));

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var window = TimeSpan.FromMinutes(options.CurrentValue.Jobs.RetentionMinutes);
                var deleted = await SweepOnceForTest(index, window, stoppingToken).ConfigureAwait(false);
                if (deleted > 0)
                    logger.LogInformation("JobRetentionSweeper pruned {Count} terminal job(s)", deleted);
            }
            catch (Exception ex) when (!IsTrueShutdown(ex, stoppingToken))
            {
                logger.LogError(ex, "JobRetentionSweeper sweep failed; will retry next tick");
            }
        }
    }

    // Static seam so tests can call a single sweep without starting the timer loop.
    internal static Task<int> SweepOnceForTest(IHistoryIndex index, TimeSpan window, CancellationToken ct) =>
        index.DeleteTerminalJobsBefore(DateTimeOffset.UtcNow - window, ct);
}
