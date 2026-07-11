using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Domain;
using Cronos;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.WebApi.Collection;

/// <summary>
/// Refreshes per-symbol funding cap/floor metadata daily by overwriting it on the existing
/// <c>funding-rate</c> feed entry. Schedule key <c>"funding-info"</c> overrides the default
/// 24h cadence. Assets without a registered funding-rate entry are skipped.
/// </summary>
internal sealed class FundingInfoRefreshService(
    IFundingInfoFetcher fetcher,
    ISchemaManager schemaManager,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<FundingInfoRefreshService> logger) : BackgroundService
{
    private const string ScheduleKey = "funding-info";
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FundingInfoRefreshService started");

        await SafeRefreshAsync(stoppingToken).ConfigureAwait(false);

        var schedules = options.CurrentValue.Schedules;
        if (schedules.TryGetValue(ScheduleKey, out var schedule))
            await RunCronLoopAsync(schedule, stoppingToken).ConfigureAwait(false);
        else
            await RunPeriodicLoopAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task RunPeriodicLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(DefaultInterval);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            await SafeRefreshAsync(ct).ConfigureAwait(false);
    }

    private async Task RunCronLoopAsync(CollectionSchedule schedule, CancellationToken ct)
    {
        var cron = CronExpression.Parse(schedule.Cron);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(schedule.TimeZone);

        while (!ct.IsCancellationRequested)
        {
            var utcNow = DateTime.UtcNow;
            var next = cron.GetNextOccurrence(utcNow, tz);
            if (next is null)
            {
                logger.LogWarning("FundingInfoRefreshService: no future cron occurrence, stopping");
                return;
            }

            await Task.Delay(next.Value - utcNow, ct).ConfigureAwait(false);
            await SafeRefreshAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task SafeRefreshAsync(CancellationToken ct)
    {
        try
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsTrueShutdown(ex, ct))
        {
            logger.LogError(ex, "FundingInfoRefreshService cycle failed");
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var entries = await fetcher.FetchAsync(ct).ConfigureAwait(false);
        if (entries.Count == 0)
        {
            logger.LogInformation("Funding info refresh: 0 entries returned");
            return;
        }

        var bySymbol = entries.ToDictionary(e => e.Symbol, StringComparer.OrdinalIgnoreCase);
        var config = options.CurrentValue;
        int written = 0;
        int skipped = 0;

        foreach (var asset in config.Assets)
        {
            if (!AssetTypes.IsFutures(asset.Type))
                continue;
            if (!bySymbol.TryGetValue(asset.Symbol, out var entry))
                continue;

            var assetDir = BackfillOrchestrator.ResolveAssetDir(
                config.DataRoot, LegacyAssetBridge.ToCollectionAsset(asset));
            var updated = await schemaManager.SetAutoApplyParams(
                assetDir,
                FeedNames.FundingRate,
                cap: entry.AdjustedFundingRateCap,
                floor: entry.AdjustedFundingRateFloor,
                intervalHours: entry.FundingIntervalHours,
                disclaimer: entry.Disclaimer,
                ct: ct);

            if (updated)
                written++;
            else
                skipped++;
        }

        logger.LogInformation(
            "Funding info refresh: {Written} updated, {Skipped} skipped (no funding-rate feed registered)",
            written, skipped);
    }

    // HttpClient timeouts surface as OperationCanceledException too — only treat the
    // stoppingToken's own cancellation as true shutdown, otherwise a timeout crashes the host.
    private static bool IsTrueShutdown(Exception ex, CancellationToken stoppingToken) =>
        ex is OperationCanceledException oce
        && stoppingToken.IsCancellationRequested
        && oce.CancellationToken == stoppingToken;
}
