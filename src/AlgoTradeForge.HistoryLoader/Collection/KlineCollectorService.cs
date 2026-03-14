using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Collection;

internal sealed class KlineCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<KlineCollectorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("KlineCollectorService started");

        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));

        // Run immediately on startup, then daily
        do
        {
            if (circuitBreaker.IsTripped)
                return;

            try
            {
                await CollectAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "KlineCollectorService cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectAsync(CancellationToken ct)
    {
        var config = options.CurrentValue;

        foreach (var asset in config.Assets)
        {
            if (circuitBreaker.IsTripped)
                return;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);

            var candleFeeds = asset.Feeds
                .Where(f => f.Enabled && f.Name == "candles")
                .ToList();

            foreach (var feed in candleFeeds)
            {
                try
                {
                    // Catch up from last written timestamp to now
                    var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var fromMs = new DateTimeOffset(asset.HistoryStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
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
                    logger.LogError(ex, "Kline collection failed for {Symbol}/{Interval}",
                        asset.Symbol, feed.Interval);
                }
            }
        }
    }
}
