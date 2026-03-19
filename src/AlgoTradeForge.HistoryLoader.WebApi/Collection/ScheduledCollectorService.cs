using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Cronos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

internal abstract class ScheduledCollectorService(
    SymbolCollector symbolCollector,
    ICollectionCircuitBreaker circuitBreaker,
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }
    protected abstract string ServiceName { get; }
    protected abstract string[] CollectedFeedNames { get; }
    protected virtual bool FuturesOnly => true;

    /// <summary>
    /// Optional schedule name from HistoryLoaderOptions.Schedules.
    /// Null (default) = 24/7 PeriodicTimer mode.
    /// </summary>
    protected virtual string? ScheduleName => null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{ServiceName} started", ServiceName);

        if (ScheduleName is { } name
            && options.CurrentValue.Schedules.TryGetValue(name, out var schedule))
        {
            await ExecuteCronAsync(schedule, stoppingToken);
        }
        else
        {
            if (ScheduleName is not null)
                logger.LogWarning(
                    "{ServiceName}: schedule '{Schedule}' not found, falling back to periodic",
                    ServiceName, ScheduleName);

            await ExecutePeriodicAsync(stoppingToken);
        }
    }

    private async Task ExecutePeriodicAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        do
        {
            if (circuitBreaker.IsTripped)
            {
                await HandleTrippedBreakerAsync(stoppingToken);
                continue;
            }

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

    private async Task ExecuteCronAsync(CollectionSchedule schedule, CancellationToken ct)
    {
        var cron = CronExpression.Parse(schedule.Cron);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);

        while (!ct.IsCancellationRequested)
        {
            var utcNow = DateTime.UtcNow;
            var next = cron.GetNextOccurrence(utcNow, tz);

            if (next is null)
            {
                logger.LogWarning("{ServiceName}: no future cron occurrence, stopping", ServiceName);
                return;
            }

            var delay = next.Value - utcNow;
            logger.LogInformation(
                "{ServiceName}: next run at {NextUtc:u} (in {Delay})",
                ServiceName, next.Value, delay);

            await Task.Delay(delay, ct);

            if (circuitBreaker.IsTripped)
            {
                await HandleTrippedBreakerAsync(ct);
                continue;
            }

            try
            {
                await CollectCycleAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "{ServiceName} cycle failed", ServiceName);
            }
        }
    }

    private async Task HandleTrippedBreakerAsync(CancellationToken ct)
    {
        if (circuitBreaker.IsAutoResettable)
        {
            var probeInterval = options.CurrentValue.NetworkProbeIntervalSeconds;
            logger.LogWarning(
                "{ServiceName} paused — network unreachable, probing every {Interval}s",
                ServiceName, probeInterval);

            while (circuitBreaker.IsTripped && circuitBreaker.IsAutoResettable && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(probeInterval), ct);

                if (await ProbeConnectivityAsync(ct))
                {
                    circuitBreaker.Reset();
                    logger.LogInformation("{ServiceName} — connectivity restored, resuming collection", ServiceName);
                    return;
                }
            }
        }
        else
        {
            var cooldown = options.CurrentValue.CircuitBreakerCooldownMinutes;
            logger.LogWarning(
                "{ServiceName} paused — circuit breaker tripped, retrying in {Cooldown} min",
                ServiceName, cooldown);
            await Task.Delay(TimeSpan.FromMinutes(cooldown), ct);
        }
    }

    private async Task<bool> ProbeConnectivityAsync(CancellationToken ct)
    {
        try
        {
            var baseUrl = options.CurrentValue.Binance.FuturesBaseUrl;
            using var client = httpClientFactory.CreateClient("connectivity-probe");
            client.Timeout = TimeSpan.FromSeconds(5);

            using var response = await client.GetAsync($"{baseUrl}/fapi/v1/ping", ct);
            // Any HTTP response means the network is reachable
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task CollectCycleAsync(CancellationToken ct)
    {
        var config = options.CurrentValue;
        var consecutiveNetworkFailures = 0;

        foreach (var asset in config.Assets)
        {
            if (circuitBreaker.IsTripped)
                return;

            if (FuturesOnly && !AssetTypes.IsFutures(asset.Type))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(config.DataRoot, asset);

            foreach (var feedName in CollectedFeedNames)
            {
                var feeds = asset.Feeds
                    .Where(f => f.Enabled && f.Name == feedName);

                foreach (var feed in feeds)
                {
                    try
                    {
                        var toMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        var from = feed.HistoryStart ?? asset.HistoryStart;
                        var fromMs = new DateTimeOffset(
                            from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                            .ToUnixTimeMilliseconds();

                        await symbolCollector.CollectFeedAsync(asset, feed, assetDir, fromMs, toMs, ct);
                        consecutiveNetworkFailures = 0;
                    }
                    catch (HttpRequestException ex) when (ex.StatusCode == (System.Net.HttpStatusCode)418)
                    {
                        circuitBreaker.Trip("IP banned by Binance");
                        return;
                    }
                    catch (Exception ex) when (
                        ex is not OperationCanceledException && NetworkErrorHelper.IsNetworkError(ex))
                    {
                        consecutiveNetworkFailures++;

                        if (consecutiveNetworkFailures >= config.NetworkFailureThreshold)
                        {
                            logger.LogError(
                                "Network unreachable after {Count} consecutive failures, tripping circuit breaker",
                                consecutiveNetworkFailures);
                            circuitBreaker.Trip("Network unreachable", TripReason.Network);
                            return;
                        }

                        logger.LogWarning(ex,
                            "Network error for {Feed}/{Symbol} ({Count}/{Threshold})",
                            feedName, asset.Symbol, consecutiveNetworkFailures, config.NetworkFailureThreshold);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        consecutiveNetworkFailures = 0;
                        logger.LogError(ex, "{Feed} collection failed for {Symbol}", feedName, asset.Symbol);
                    }
                }
            }
        }
    }
}
