using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class FundingRateFeedCollector(
    IFeedFetcherFactory feedFetcherFactory,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<FundingRateFeedCollector> logger)
    : FeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    private static readonly string[] FundingRateColumns = ["rate", "mark_price"];

    public override string FeedName => FeedNames.FundingRate;

    public override async Task CollectAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        // Ensure feeds.json schema with auto-apply config
        SchemaManager.EnsureSchema(assetDir, FeedNames.FundingRate, "", FundingRateColumns,
            new AutoApplySpec("FundingRate", "rate"));

        // Resume from last written timestamp
        var resumeTs = FeedWriter.ResumeFrom(assetDir, FeedNames.FundingRate, "");
        if (resumeTs.HasValue && resumeTs.Value >= fromMs)
            fromMs = resumeTs.Value + 1;

        var fetcher = feedFetcherFactory.Create(ExchangeKeys.Futures(assetConfig.Exchange), FeedNames.FundingRate);

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;
        long previousTs = 0;
        var gaps = new List<DataGap>();
        const long fundingIntervalMs = 8 * 60 * 60 * 1000L; // 8 hours

        await foreach (var record in fetcher.FetchAsync(
            assetConfig.Symbol, null, fromMs, toMs, ct))
        {
            try
            {
                FeedWriter.Write(assetDir, FeedNames.FundingRate, "", FundingRateColumns, record);
            }
            catch (IOException ex)
            {
                Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", FeedNames.FundingRate, assetDir);
                UpdateFeedStatus(assetDir, FeedNames.FundingRate, "", firstTs, lastTs, recordCount,
                    CollectionHealth.Error, gaps);
                throw;
            }

            DetectGap(record.TimestampMs, previousTs, fundingIntervalMs, feedConfig.GapThresholdMultiplier, gaps);
            previousTs = record.TimestampMs;

            firstTs ??= record.TimestampMs;
            lastTs = record.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
            UpdateFeedStatus(assetDir, FeedNames.FundingRate, "", firstTs, lastTs, recordCount,
                newGaps: gaps);

        Logger.LogInformation(
            "Collected {Count} funding rate records for {Symbol}",
            recordCount, assetConfig.Symbol);
    }
}
