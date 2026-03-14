using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class CandleFeedCollector(
    IFuturesDataFetcher futuresClient,
    ISpotDataFetcher? spotClient,
    ICandleWriter candleWriter,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<CandleFeedCollector> logger)
    : FeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    private static readonly string[] CandleExtColumns =
        ["quote_vol", "trade_count", "taker_buy_vol", "taker_buy_quote_vol"];

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

        bool isSpot = assetConfig.Type == "spot";

        if (isSpot && spotClient is null)
            throw new InvalidOperationException(
                $"Spot data fetcher is not registered but asset {assetConfig.Symbol} is type 'spot'.");

        // Ensure candle-ext schema only for non-spot assets.
        if (!isSpot)
        {
            SchemaManager.EnsureSchema(assetDir, FeedNames.CandleExt, interval, CandleExtColumns);
        }

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();
        long expectedMs = ComputeExpectedMs(interval);

        // Route to spot or futures client based on asset type.
        IAsyncEnumerable<KlineRecord> klineSource = isSpot
            ? spotClient!.FetchKlinesAsync(assetConfig.Symbol, interval, fromMs, toMs, ct)
            : futuresClient.FetchKlinesAsync(assetConfig.Symbol, interval, fromMs, toMs, ct);

        await foreach (var kline in klineSource)
        {
            try
            {
                candleWriter.Write(assetDir, interval, kline, assetConfig.DecimalDigits);
            }
            catch (IOException ex)
            {
                Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", FeedNames.Candles, assetDir);
                UpdateFeedStatus(assetDir, FeedNames.Candles, interval, firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps);
                throw;
            }

            // Write extended fields as double feed (futures only).
            if (!isSpot)
            {
                var extRecord = new FeedRecord(kline.TimestampMs,
                [
                    (double)kline.QuoteVolume,
                    kline.TradeCount,
                    (double)kline.TakerBuyVolume,
                    (double)kline.TakerBuyQuoteVolume
                ]);
                try
                {
                    FeedWriter.Write(assetDir, FeedNames.CandleExt, interval, CandleExtColumns, extRecord);
                }
                catch (IOException ex)
                {
                    Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", FeedNames.CandleExt, assetDir);
                    UpdateFeedStatus(assetDir, FeedNames.CandleExt, interval, firstTs, lastTs, recordCount,
                        CollectionHealth.Error, gaps);
                    throw;
                }
            }

            DetectGap(kline.TimestampMs, previousTs, expectedMs, feedConfig.GapThresholdMultiplier, gaps);
            previousTs = kline.TimestampMs;

            firstTs ??= kline.TimestampMs;
            lastTs = kline.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
        {
            UpdateFeedStatus(assetDir, FeedNames.Candles, interval, firstTs, lastTs, recordCount,
                newGaps: gaps);
            if (!isSpot)
                UpdateFeedStatus(assetDir, FeedNames.CandleExt, interval, firstTs, lastTs, recordCount,
                    newGaps: gaps);
        }

        Logger.LogInformation(
            "Collected {Count} candle records for {Symbol}/{Interval}",
            recordCount, assetConfig.Symbol, interval);
    }
}
