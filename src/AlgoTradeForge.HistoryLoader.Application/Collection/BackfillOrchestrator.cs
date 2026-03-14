using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class BackfillOrchestrator(
    SymbolCollector symbolCollector,
    IOptions<HistoryLoaderOptions> options,
    ILogger<BackfillOrchestrator> logger)
{
    private readonly HashSet<string> _runningSymbols = [];
    private readonly Lock _lock = new();

    public bool IsRunning(string symbolDir)
    {
        lock (_lock)
            return _runningSymbols.Contains(symbolDir);
    }

    public async Task RunAsync(
        IReadOnlyList<AssetCollectionConfig> assets,
        IReadOnlyList<string>? feedFilter = null,
        DateOnly? fromDate = null,
        CancellationToken ct = default)
    {
        var config = options.Value;
        using var semaphore = new SemaphoreSlim(config.MaxBackfillConcurrency);

        var tasks = assets.Select(asset => BackfillSymbolAsync(
            asset, semaphore, config.DataRoot, feedFilter, fromDate, ct));

        await Task.WhenAll(tasks);
    }

    public async Task RunSingleAsync(
        AssetCollectionConfig asset,
        string assetDir,
        IReadOnlyList<string>? feedFilter = null,
        DateOnly? fromDate = null,
        CancellationToken ct = default)
    {
        var config = options.Value;

        lock (_lock)
        {
            if (!_runningSymbols.Add(assetDir))
                throw new InvalidOperationException($"Backfill already running for {assetDir}");
        }

        try
        {
            var from = fromDate ?? asset.HistoryStart;
            var fromMs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                .ToUnixTimeMilliseconds();
            var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var feeds = asset.Feeds
                .Where(f => f.Enabled)
                .Where(f => feedFilter is null || feedFilter.Contains(f.Name))
                .ToList();

            foreach (var feed in feeds)
            {
                await symbolCollector.CollectFeedAsync(asset, feed, assetDir, fromMs, toMs, ct);
            }
        }
        finally
        {
            lock (_lock)
                _runningSymbols.Remove(assetDir);
        }
    }

    private async Task BackfillSymbolAsync(
        AssetCollectionConfig asset,
        SemaphoreSlim semaphore,
        string dataRoot,
        IReadOnlyList<string>? feedFilter,
        DateOnly? fromDate,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var assetDir = ResolveAssetDir(dataRoot, asset);
            await RunSingleAsync(asset, assetDir, feedFilter, fromDate, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backfill failed for {Symbol}", asset.Symbol);
        }
        finally
        {
            semaphore.Release();
        }
    }

    public static string ResolveAssetDir(string dataRoot, AssetCollectionConfig asset) =>
        Path.Combine(dataRoot, asset.Exchange, AssetPathConvention.DirectoryName(asset.Symbol, asset.Type));
}
