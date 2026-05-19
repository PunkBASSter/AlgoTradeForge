using AlgoTradeForge.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// On boot, runs <see cref="AggregatedDirSweeper.Sweep"/> over every asset dir under
/// <c>HistoryLoaderOptions.DataRoot</c>. MUST be registered before feed-collection hosted
/// services so orphan staging dirs from interrupted aggregations are cleared before workers
/// start. Asset enumeration uses <see cref="IFileStorage.ListKeys"/> and derives immediate
/// subdirectories from key prefixes (object stores have no real directories).
/// </summary>
public sealed class StartupSweepService(
    AggregatedDirSweeper sweeper,
    IFileStorage storage,
    IOptions<HistoryLoaderOptions> options,
    ILogger<StartupSweepService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dataRoot = options.Value.DataRoot;

        var assetDirCount = 0;
        foreach (var assetDir in await EnumerateAssetDirs(dataRoot, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sweeper.Sweep(assetDir, cancellationToken);
            assetDirCount++;
        }

        logger.LogInformation(
            "Startup sweep complete: scanned {AssetCount} asset directories under {DataRoot}.",
            assetDirCount, dataRoot);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<List<string>> EnumerateAssetDirs(string dataRoot, CancellationToken ct)
    {
        var assetDirs = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var key in storage.ListKeys(dataRoot, suffix: null, recursive: true, ct))
        {
            var rel = key.Substring(dataRoot.Length).TrimStart('/', Path.DirectorySeparatorChar);
            var firstSlash = rel.IndexOfAny(['/', Path.DirectorySeparatorChar]);
            if (firstSlash <= 0) continue;
            var afterExchange = rel.Substring(firstSlash + 1);
            var secondSlash = afterExchange.IndexOfAny(['/', Path.DirectorySeparatorChar]);
            if (secondSlash <= 0) continue;

            var exchange = rel.Substring(0, firstSlash);
            var asset = afterExchange.Substring(0, secondSlash);
            assetDirs.Add(Path.Combine(dataRoot, exchange, asset));
        }
        return assetDirs.ToList();
    }
}
