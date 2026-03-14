using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class FundingRateFeedCollector(
    IServiceProvider serviceProvider,
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

        var key = ExchangeKeys.Futures(assetConfig.Exchange);
        var fetcher = serviceProvider.GetRequiredKeyedService<IFundingRateFetcher>(key);

        long recordCount = 0;
        long? firstTs = null;
        long lastTs = 0;

        await foreach (var record in fetcher.FetchFundingRatesAsync(
            assetConfig.Symbol, fromMs, toMs, ct))
        {
            try
            {
                FeedWriter.Write(assetDir, FeedNames.FundingRate, "", FundingRateColumns, record);
            }
            catch (IOException ex)
            {
                Logger.LogCritical(ex, "Disk I/O error writing {Feed} for {AssetDir}", FeedNames.FundingRate, assetDir);
                UpdateFeedStatus(assetDir, FeedNames.FundingRate, "", firstTs, lastTs, recordCount,
                    CollectionHealth.Error);
                throw;
            }

            firstTs ??= record.TimestampMs;
            lastTs = record.TimestampMs;
            recordCount++;
        }

        if (recordCount > 0)
            UpdateFeedStatus(assetDir, FeedNames.FundingRate, "", firstTs, lastTs, recordCount);

        Logger.LogInformation(
            "Collected {Count} funding rate records for {Symbol}",
            recordCount, assetConfig.Symbol);
    }
}
