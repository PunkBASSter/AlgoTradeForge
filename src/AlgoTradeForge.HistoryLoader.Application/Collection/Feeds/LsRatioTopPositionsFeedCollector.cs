using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class LsRatioTopPositionsFeedCollector(
    IServiceProvider serviceProvider,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<LsRatioTopPositionsFeedCollector> logger)
    : GenericFeedCollectorBase(serviceProvider, feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.LsRatioTopPositions;
    protected override string[] Columns => ["long_pct", "short_pct", "ratio"];
}
