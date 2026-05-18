using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// On boot, runs <see cref="AggregatedDirSweeper.Sweep"/> over every asset dir. MUST be
/// registered before feed-collection hosted services so orphan staging dirs from interrupted
/// aggregations are cleared before workers start.
/// </summary>
public sealed class StartupSweepService(
    AggregatedDirSweeper sweeper,
    IOptions<HistoryLoaderOptions> options,
    ILogger<StartupSweepService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dataRoot = options.Value.DataRoot;
        if (!Directory.Exists(dataRoot))
        {
            logger.LogInformation(
                "Startup sweep: dataRoot {DataRoot} does not exist; nothing to sweep.",
                dataRoot);
            return;
        }

        var assetDirCount = 0;
        foreach (var exchangeDir in Directory.EnumerateDirectories(dataRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var assetDir in Directory.EnumerateDirectories(exchangeDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await sweeper.Sweep(assetDir, cancellationToken);
                assetDirCount++;
            }
        }

        logger.LogInformation(
            "Startup sweep complete: scanned {AssetCount} asset directories under {DataRoot}.",
            assetDirCount, dataRoot);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
