using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Collection;

internal sealed class FundingRateCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<FundingRateCollectorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FundingRateCollectorService started");

        using var timer = new PeriodicTimer(TimeSpan.FromHours(8));

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
                logger.LogError(ex, "FundingRateCollectorService cycle failed");
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

            if (asset.Type is not ("perpetual" or "future"))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);

            var fundingFeed = asset.Feeds
                .FirstOrDefault(f => f.Enabled && f.Name == "funding-rate");

            if (fundingFeed is null)
                continue;

            try
            {
                var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var fromMs = new DateTimeOffset(asset.HistoryStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                    .ToUnixTimeMilliseconds();

                await symbolCollector.CollectFeedAsync(asset, fundingFeed, assetDir, fromMs, toMs, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == (System.Net.HttpStatusCode)418)
            {
                circuitBreaker.Trip("IP banned by Binance");
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Funding rate collection failed for {Symbol}", asset.Symbol);
            }
        }
    }
}
