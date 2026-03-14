using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Collection;

internal abstract class ScheduledCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }
    protected abstract string ServiceName { get; }
    protected abstract string[] CollectedFeedNames { get; }
    protected virtual bool FuturesOnly => true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{ServiceName} started", ServiceName);

        using var timer = new PeriodicTimer(Interval);

        do
        {
            if (circuitBreaker.IsTripped)
                return;

            try
            {
                await CollectCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{ServiceName} cycle failed", ServiceName);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectCycleAsync(CancellationToken ct)
    {
        var config = options.CurrentValue;

        foreach (var asset in config.Assets)
        {
            if (circuitBreaker.IsTripped)
                return;

            if (FuturesOnly && asset.Type is not ("perpetual" or "future"))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);

            foreach (var feedName in CollectedFeedNames)
            {
                var feed = asset.Feeds
                    .FirstOrDefault(f => f.Enabled && f.Name == feedName);

                if (feed is null)
                    continue;

                try
                {
                    var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var fromMs = new DateTimeOffset(
                        asset.HistoryStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                        .ToUnixTimeMilliseconds();

                    await symbolCollector.CollectFeedAsync(asset, feed, assetDir, fromMs, toMs, ct);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == (System.Net.HttpStatusCode)418)
                {
                    circuitBreaker.Trip("IP banned by Binance");
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "{Feed} collection failed for {Symbol}", feedName, asset.Symbol);
                }
            }
        }
    }
}
