using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class LsRatioGlobalFeedCollector(
    IFuturesDataFetcher futuresClient,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<LsRatioGlobalFeedCollector> logger)
    : GenericFeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.LsRatioGlobal;
    protected override string[] Columns => ["long_pct", "short_pct", "ratio"];

    protected override IAsyncEnumerable<FeedRecord> FetchAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct) =>
        futuresClient.FetchGlobalLongShortRatioAsync(symbol, interval, fromMs, toMs, ct);
}
