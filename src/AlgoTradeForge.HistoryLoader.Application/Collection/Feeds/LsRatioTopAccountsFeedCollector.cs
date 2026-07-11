using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class LsRatioTopAccountsFeedCollector(
    IFeedFetcherFactory feedFetcherFactory,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<LsRatioTopAccountsFeedCollector> logger)
    : GenericFeedCollectorBase(feedFetcherFactory, feedWriter, schemaManager, feedStatusStore, options, logger)
{
    public override string FeedName => FeedNames.LsRatioTopAccounts;
    protected override string[] Columns => ["long_pct", "short_pct", "ratio"];
}
