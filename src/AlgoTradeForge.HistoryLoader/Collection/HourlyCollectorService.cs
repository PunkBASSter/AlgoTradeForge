using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Collection;

internal sealed class HourlyCollectorService(
    SymbolCollector symbolCollector,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<HourlyCollectorService> logger) : BackgroundService
{
    private static readonly string[] HourlyFeedNames =
        ["mark-price", "ls-ratio-top-positions"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("HourlyCollectorService started");

        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));

        // Run immediately on startup, then every hour
        do
        {
            try
            {
                await CollectAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "HourlyCollectorService cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectAsync(CancellationToken ct)
    {
        var config = options.CurrentValue;

        bool ipBanned = false;
        foreach (var asset in config.Assets)
        {
            if (asset.Type is not ("perpetual" or "future"))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);

            foreach (var feedName in HourlyFeedNames)
            {
                var feed = asset.Feeds
                    .FirstOrDefault(f => f.Enabled && f.Name == feedName);

                if (feed is null)
                    continue;

                try
                {
                    var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var fromMs = new DateTimeOffset(asset.HistoryStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                        .ToUnixTimeMilliseconds();

                    await symbolCollector.CollectFeedAsync(asset, feed, assetDir, fromMs, toMs, ct);
                }
                catch (HttpRequestException ex) when (ex.Message.Contains("418"))
                {
                    logger.LogCritical(ex, "IP banned by Binance — stopping all collection");
                    ipBanned = true;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "{Feed} collection failed for {Symbol}", feedName, asset.Symbol);
                }
            }

            if (ipBanned)
                break;
        }
    }
}
