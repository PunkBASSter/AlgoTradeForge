using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class IndexPriceFeedCollector(
    IFeedFetcherFactory feedFetcherFactory,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<IndexPriceFeedCollector> logger)
    : GenericFeedCollectorBase(feedFetcherFactory, feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.IndexPrice;
    protected override string[] Columns => ["o", "h", "l", "c"];
}
