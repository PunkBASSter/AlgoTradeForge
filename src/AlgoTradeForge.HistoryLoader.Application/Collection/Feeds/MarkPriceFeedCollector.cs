using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class MarkPriceFeedCollector(
    IServiceProvider serviceProvider,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<MarkPriceFeedCollector> logger)
    : GenericFeedCollectorBase(serviceProvider, feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.MarkPrice;
    protected override string[] Columns => ["o", "h", "l", "c"];
}
