using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class OpenInterestFeedCollector(
    IFeedFetcherFactory feedFetcherFactory,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<OpenInterestFeedCollector> logger)
    : GenericFeedCollectorBase(feedFetcherFactory, feedWriter, schemaManager, feedStatusStore, options, logger)
{
    public override string FeedName => FeedNames.OpenInterest;
    protected override string[] Columns => ["oi", "oi_usd"];
}
