using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class FundingRateFeedCollector(
    IFuturesDataFetcher futuresClient,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<FundingRateFeedCollector> logger)
    : FeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    private static readonly string[] FundingRateColumns = ["rate", "mark_price"];

    public override string FeedName => "funding-rate";

    public override async Task CollectAsync(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        long fromMs,
        long toMs,
        CancellationToken ct)
    {
        // Ensure feeds.json schema with auto-apply config
        SchemaManager.EnsureSchema(assetDir, "funding-rate", "", FundingRateColumns,
            new AutoApplySpec("FundingRate", "rate"));

        // Resume from last written timestamp
        var resumeTs = FeedWriter.ResumeFrom(assetDir, "funding-rate", "");
        if (resumeTs.HasValue && resumeTs.Value >= fromMs)
            fromMs = resumeTs.Value + 1;

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;

        await foreach (var record in futuresClient.FetchFundingRatesAsync(
            assetConfig.Symbol, fromMs, toMs, ct))
        {
            try
            {
                FeedWriter.Write(assetDir, "funding-rate", "", FundingRateColumns, record);
            }
            catch (IOException ex)
            {
                Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", "funding-rate", assetDir);
                UpdateFeedStatus(assetDir, "funding-rate", "", firstTs, lastTs, recordCount,
                    CollectionHealth.Error);
                throw;
            }

            firstTs ??= record.TimestampMs;
            lastTs = record.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
            UpdateFeedStatus(assetDir, "funding-rate", "", firstTs, lastTs, recordCount);

        Logger.LogInformation(
            "Collected {Count} funding rate records for {Symbol}",
            recordCount, assetConfig.Symbol);
    }
}
