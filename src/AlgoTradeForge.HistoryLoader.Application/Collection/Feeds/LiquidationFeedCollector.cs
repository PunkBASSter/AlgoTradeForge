using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class LiquidationFeedCollector(
    IServiceProvider serviceProvider,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<LiquidationFeedCollector> logger)
    : GenericFeedCollectorBase(serviceProvider, feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.Liquidations;
    protected override string[] Columns => ["side", "price", "qty", "notional_usd"];
}
