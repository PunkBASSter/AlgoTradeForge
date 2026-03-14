using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Collection;

internal sealed class OiCollectorService(
    SymbolCollector symbolCollector,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<OiCollectorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OiCollectorService started");

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));

        do
        {
            try
            {
                await CollectAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "OiCollectorService cycle failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CollectAsync(CancellationToken ct)
    {
        var config = options.CurrentValue;

        foreach (var asset in config.Assets)
        {
            if (asset.Type is not ("perpetual" or "future"))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);

            var oiFeed = asset.Feeds
                .FirstOrDefault(f => f.Enabled && f.Name == "open-interest");

            if (oiFeed is null)
                continue;

            try
            {
                var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var fromMs = new DateTimeOffset(asset.HistoryStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                    .ToUnixTimeMilliseconds();

                await symbolCollector.CollectFeedAsync(asset, oiFeed, assetDir, fromMs, toMs, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == (System.Net.HttpStatusCode)418)
            {
                logger.LogCritical(ex, "IP banned by Binance — stopping all collection");
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Open interest collection failed for {Symbol}", asset.Symbol);
            }
        }
    }
}
