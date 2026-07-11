using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class CandleFeedCollector(
    ICandleFetcherFactory candleFetcherFactory,
    ICandleWriter candleWriter,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<CandleFeedCollector> logger)
    : FeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.Candles;
    public override bool SupportsSpot => true;

    public override async Task Collect(
        CollectionAsset asset,
        CollectionFeed feed,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct = default)
    {
        var interval = feed.Interval;

        // Ensure feeds.json has candle config
        await SchemaManager.EnsureCandleConfig(assetDir, asset.DecimalDigits, interval, ct);

        // Resume from last written timestamp
        var resumeTs = await candleWriter.ResumeFrom(assetDir, interval, ct);
        if (resumeTs.HasValue && resumeTs.Value >= fromMs)
            fromMs = resumeTs.Value + 1;

        // Resolve the kline fetcher via factory (handles spot/futures routing).
        var klineFetcher = candleFetcherFactory.Create(ExchangeKeys.Resolve(asset));

        // Determine ext columns from the fetcher — null means no ext feed.
        var extColumns = klineFetcher.CandleExtColumns;

        if (extColumns is not null)
        {
            await SchemaManager.EnsureSchema(assetDir, FeedNames.CandleExt, interval, extColumns, ct: ct);
        }

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();
        long expectedMs = ComputeExpectedMs(interval);

        await foreach (var candle in klineFetcher.FetchCandlesAsync(
            asset.Venue.ApiSymbol, interval, fromMs, toMs, ct))
        {
            try
            {
                candleWriter.Write(assetDir, interval, candle, asset.DecimalDigits);
            }
            catch (IOException ex)
            {
                Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", FeedNames.Candles, assetDir);
                await UpdateFeedStatus(assetDir, FeedNames.Candles, interval, firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps, ct);
                throw;
            }

            // Write extended fields as double feed when ext columns are available.
            if (extColumns is not null && candle.ExtValues is not null)
            {
                var extRecord = new FeedRecord(candle.TimestampMs, candle.ExtValues);
                try
                {
                    FeedWriter.Write(assetDir, FeedNames.CandleExt, interval, extColumns, extRecord);
                }
                catch (IOException ex)
                {
                    Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", FeedNames.CandleExt, assetDir);
                    await UpdateFeedStatus(assetDir, FeedNames.CandleExt, interval, firstTs, lastTs, recordCount,
                        CollectionHealth.Error, gaps, ct);
                    throw;
                }
            }

            DetectGap(candle.TimestampMs, previousTs, expectedMs, options.CurrentValue.GapThresholdMultiplier, gaps);
            previousTs = candle.TimestampMs;

            firstTs ??= candle.TimestampMs;
            lastTs = candle.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
        {
            await UpdateFeedStatus(assetDir, FeedNames.Candles, interval, firstTs, lastTs, recordCount,
                newGaps: gaps, ct: ct);
            if (extColumns is not null)
                await UpdateFeedStatus(assetDir, FeedNames.CandleExt, interval, firstTs, lastTs, recordCount,
                    newGaps: gaps, ct: ct);
        }

        if (recordCount > 0)
            Logger.LogInformation(
                "Collected {Count} candle records for {Symbol}/{Interval}",
                recordCount, asset.Venue.ApiSymbol, interval);
        else
            Logger.LogDebug(
                "Collected 0 candle records for {Symbol}/{Interval}",
                asset.Venue.ApiSymbol, interval);
    }
}
