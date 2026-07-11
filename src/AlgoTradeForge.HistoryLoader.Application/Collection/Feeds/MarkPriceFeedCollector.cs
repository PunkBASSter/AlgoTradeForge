using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class MarkPriceFeedCollector(
    IFeedFetcherFactory feedFetcherFactory,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<MarkPriceFeedCollector> logger)
    : GenericFeedCollectorBase(feedFetcherFactory, feedWriter, schemaManager, feedStatusStore, options, logger)
{
    public override string FeedName => FeedNames.MarkPrice;
    protected override string[] Columns => ["o", "h", "l", "c"];
}
