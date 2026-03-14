using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class MarkPriceFeedCollector(
    IFuturesDataFetcher futuresClient,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<MarkPriceFeedCollector> logger)
    : FeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    private static readonly string[] MarkPriceColumns = ["o", "h", "l", "c"];

    public override string FeedName => "mark-price";

    public override async Task CollectAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        var interval = feedConfig.Interval;

        SchemaManager.EnsureSchema(assetDir, "mark-price", interval, MarkPriceColumns);

        var resumeTs = FeedWriter.ResumeFrom(assetDir, "mark-price", interval);
        if (resumeTs.HasValue && resumeTs.Value >= fromMs)
            fromMs = resumeTs.Value + 1;

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();
        long expectedMs = ComputeExpectedMs(interval);

        await foreach (var kline in futuresClient.FetchMarkPriceKlinesAsync(
            assetConfig.Symbol, interval, fromMs, toMs, ct))
        {
            var feedRecord = new FeedRecord(kline.TimestampMs,
            [
                (double)kline.Open,
                (double)kline.High,
                (double)kline.Low,
                (double)kline.Close
            ]);

            try
            {
                FeedWriter.Write(assetDir, "mark-price", interval, MarkPriceColumns, feedRecord);
            }
            catch (IOException ex)
            {
                Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", "mark-price", assetDir);
                UpdateFeedStatus(assetDir, "mark-price", interval, firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps);
                throw;
            }

            DetectGap(kline.TimestampMs, previousTs, expectedMs, feedConfig.GapThresholdMultiplier, gaps);
            previousTs = kline.TimestampMs;

            firstTs ??= kline.TimestampMs;
            lastTs = kline.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
            UpdateFeedStatus(assetDir, "mark-price", interval, firstTs, lastTs, recordCount,
                newGaps: gaps);

        Logger.LogInformation(
            "Collected {Count} mark-price records for {Symbol}/{Interval}",
            recordCount, assetConfig.Symbol, interval);
    }
}
