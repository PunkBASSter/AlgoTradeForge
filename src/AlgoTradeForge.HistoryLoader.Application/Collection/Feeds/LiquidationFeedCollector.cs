using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class LiquidationFeedCollector(
    IFuturesDataFetcher futuresClient,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<LiquidationFeedCollector> logger)
    : GenericFeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.Liquidations;
    protected override string[] Columns => ["side", "price", "qty", "notional_usd"];

    protected override IAsyncEnumerable<FeedRecord> FetchAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct) =>
        futuresClient.FetchLiquidationsAsync(symbol, fromMs, toMs, ct);
}
