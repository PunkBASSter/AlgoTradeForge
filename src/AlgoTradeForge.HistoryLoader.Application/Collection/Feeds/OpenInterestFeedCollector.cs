using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class OpenInterestFeedCollector(
    IServiceProvider serviceProvider,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<OpenInterestFeedCollector> logger)
    : GenericFeedCollectorBase(serviceProvider, feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.OpenInterest;
    protected override string[] Columns => ["oi", "oi_usd"];
}
