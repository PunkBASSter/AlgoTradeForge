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
    : GenericFeedCollectorBase(feedFetcherFactory, feedWriter, schemaManager, feedStatusStore, logger)
{
    private const long FundingIntervalMs = 8 * 60 * 60 * 1000L; // 8 hours

    public override string FeedName => FeedNames.FundingRate;
    protected override string[] Columns => ["rate", "mark_price"];

    protected override AutoApplySpec? GetAutoApplySpec() =>
        new("FundingRate", "rate");

    protected override long GetExpectedIntervalMs(string interval) => FundingIntervalMs;

    protected override string ResolveExchangeKey(AssetCollectionConfig assetConfig) =>
        ExchangeKeys.Futures(assetConfig.Exchange);
}
