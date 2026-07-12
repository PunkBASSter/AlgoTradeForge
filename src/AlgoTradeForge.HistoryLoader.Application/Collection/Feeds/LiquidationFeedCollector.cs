using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class LiquidationFeedCollector(
    IFeedFetcherFactory feedFetcherFactory,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    IOptionsMonitor<HistoryLoaderOptions> options,
    ILogger<LiquidationFeedCollector> logger)
    : GenericFeedCollectorBase(feedFetcherFactory, feedWriter, schemaManager, feedStatusStore, options, logger)
{
    public override string FeedName => FeedNames.Liquidations;
    protected override string[] Columns => ["side", "price", "qty", "notional_usd"];
}
