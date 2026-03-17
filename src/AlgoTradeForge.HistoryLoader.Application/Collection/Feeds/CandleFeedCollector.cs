using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class CandleFeedCollector(
    ICandleFetcherFactory candleFetcherFactory,
    ICandleWriter candleWriter,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<CandleFeedCollector> logger)
    : FeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.Candles;
    public override bool SupportsSpot => true;

    public override async Task CollectAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var interval = feedConfig.Interval;

        // Ensure feeds.json has candle config
        SchemaManager.EnsureCandleConfig(assetDir, assetConfig.DecimalDigits, interval);

        // Resume from last written timestamp
        var resumeTs = candleWriter.ResumeFrom(assetDir, interval);
        if (resumeTs.HasValue && resumeTs.Value >= fromMs)
            fromMs = resumeTs.Value + 1;

        // Resolve the kline fetcher via factory (handles spot/futures routing).
        var klineFetcher = candleFetcherFactory.Create(ExchangeKeys.Resolve(assetConfig));

        // Determine ext columns from the fetcher — null means no ext feed.
        var extColumns = klineFetcher.CandleExtColumns;

        if (extColumns is not null)
        {
            SchemaManager.EnsureSchema(assetDir, FeedNames.CandleExt, interval, extColumns);
        }

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();
        long expectedMs = ComputeExpectedMs(interval);

        await foreach (var candle in klineFetcher.FetchCandlesAsync(
            assetConfig.Symbol, interval, fromMs, toMs, ct))
        {
            try
            {
                candleWriter.Write(assetDir, interval, candle, assetConfig.DecimalDigits);
            }
            catch (IOException ex)
            {
                Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", FeedNames.Candles, assetDir);
                UpdateFeedStatus(assetDir, FeedNames.Candles, interval, firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps);
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
                    UpdateFeedStatus(assetDir, FeedNames.CandleExt, interval, firstTs, lastTs, recordCount,
                        CollectionHealth.Error, gaps);
                    throw;
                }
            }

            DetectGap(candle.TimestampMs, previousTs, expectedMs, feedConfig.GapThresholdMultiplier, gaps);
            previousTs = candle.TimestampMs;

            firstTs ??= candle.TimestampMs;
            lastTs = candle.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
        {
            UpdateFeedStatus(assetDir, FeedNames.Candles, interval, firstTs, lastTs, recordCount,
                newGaps: gaps);
            if (extColumns is not null)
                UpdateFeedStatus(assetDir, FeedNames.CandleExt, interval, firstTs, lastTs, recordCount,
                    newGaps: gaps);
        }

        if (recordCount > 0)
            Logger.LogInformation(
                "Collected {Count} candle records for {Symbol}/{Interval}",
                recordCount, assetConfig.Symbol, interval);
        else
            Logger.LogDebug(
                "Collected 0 candle records for {Symbol}/{Interval}",
                assetConfig.Symbol, interval);
    }
}
