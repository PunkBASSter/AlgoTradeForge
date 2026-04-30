using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// On HistoryLoader boot, walks every <c>{dataRoot}/{exchange}/{asset}/</c> directory
/// and runs <see cref="AggregatedDirSweeper.Sweep"/> on each one. Must be registered
/// BEFORE the feed-collection hosted services so any orphan staging directories from
/// an interrupted aggregation are gone before workers start (TRD §4.1).
/// </summary>
public sealed class StartupSweepService(
    AggregatedDirSweeper sweeper,
    IOptions<HistoryLoaderOptions> options,
    ILogger<StartupSweepService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var dataRoot = options.Value.DataRoot;
        if (!Directory.Exists(dataRoot))
        {
            logger.LogInformation(
                "Startup sweep: dataRoot {DataRoot} does not exist; nothing to sweep.",
                dataRoot);
            return Task.CompletedTask;
        }

        var assetDirCount = 0;
        foreach (var exchangeDir in Directory.EnumerateDirectories(dataRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var assetDir in Directory.EnumerateDirectories(exchangeDir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                sweeper.Sweep(assetDir);
                assetDirCount++;
            }
        }

        logger.LogInformation(
            "Startup sweep complete: scanned {AssetCount} asset directories under {DataRoot}.",
            assetDirCount, dataRoot);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
