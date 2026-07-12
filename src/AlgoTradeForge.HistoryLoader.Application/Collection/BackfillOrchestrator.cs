using AlgoTradeForge.HistoryLoader.Application.Archive;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class BackfillOrchestrator(
    SymbolCollector symbolCollector,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<BackfillOrchestrator> logger) : IBackfillOrchestrator
{
    private readonly HashSet<string> _runningSymbols = [];
    private readonly Lock _lock = new();

    // Shared concurrency gate across every Run entry point (boot sweep + scheduled cycles).
    // MaxBackfillConcurrency is read once at construction — not hot-reloaded. A lazy ??= would
    // let a boot-sweep kick and a scheduled cycle each create their own semaphore on first access,
    // reintroducing the N×concurrency the shared gate fixes.
    private readonly SemaphoreSlim _semaphore = new(options.CurrentValue.MaxBackfillConcurrency);

    public bool IsRunning(string symbolDir)
    {
        lock (_lock)
            return _runningSymbols.Contains(symbolDir);
    }

    public async Task Run(
        IReadOnlyList<CollectionAsset> assets,
        IReadOnlyList<string>? feedFilter = null,
        DateOnly? fromDate = null,
        CancellationToken ct = default)
    {
        var dataRoot = options.CurrentValue.DataRoot;

        var tasks = assets.Select(asset => BackfillSymbolAsync(
            asset, dataRoot, feedFilter, fromDate, ct));

        await Task.WhenAll(tasks);
    }

    public async Task<bool> TryRunSingle(
        CollectionAsset asset,
        string assetDir,
        IReadOnlyList<string>? feedFilter = null,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default)
    {
        bool added = false;
        try
        {
            lock (_lock)
            {
                if (!_runningSymbols.Add(assetDir))
                    return false;
                added = true;
            }

            var toMs = toDate is { } d
                ? new DateTimeOffset(d.AddDays(1), TimeOnly.MinValue, TimeSpan.Zero).ToUnixTimeMilliseconds()
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var feeds = asset.Feeds
                .Where(f => feedFilter is null || feedFilter.Contains(f.FeedName))
                .ToList();

            foreach (var feed in feeds)
            {
                var from = fromDate ?? feed.EffectiveStart;
                var fromMs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                    .ToUnixTimeMilliseconds();

                await symbolCollector.CollectFeed(asset, feed, assetDir, fromMs, toMs, progress, ct);
            }

            return true;
        }
        finally
        {
            if (added)
            {
                lock (_lock)
                    _runningSymbols.Remove(assetDir);
            }
        }
    }

    private async Task BackfillSymbolAsync(
        CollectionAsset asset,
        string dataRoot,
        IReadOnlyList<string>? feedFilter,
        DateOnly? fromDate,
        CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var assetDir = ResolveAssetDir(dataRoot, asset);
            if (!await TryRunSingle(asset, assetDir, feedFilter, fromDate, ct: ct))
                logger.LogWarning("Backfill already running for {Symbol}, skipping", asset.Venue.ApiSymbol);
        }
        catch (Exception ex) when (!IsTrueShutdown(ex, ct))
        {
            // A collector's HttpClient timeout throws TaskCanceledException (an OCE) whose token is
            // NOT ct — swallow it per-asset. A naive `is not OperationCanceledException` filter lets
            // it escape Task.WhenAll and abort the remaining assets in the kick batch.
            logger.LogError(ex, "Backfill failed for {Symbol}", asset.Venue.ApiSymbol);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private static bool IsTrueShutdown(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException oce && ct.IsCancellationRequested && oce.CancellationToken == ct;

    public static string ResolveAssetDir(string dataRoot, CollectionAsset asset) =>
        Path.Combine(dataRoot, asset.Exchange, asset.Venue.Dir);
}
