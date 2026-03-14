using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class LsRatioTopPositionsFeedCollector(
    IFuturesDataFetcher futuresClient,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<LsRatioTopPositionsFeedCollector> logger)
    : GenericFeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.LsRatioTopPositions;
    protected override string[] Columns => ["long_pct", "short_pct", "ratio"];

    protected override IAsyncEnumerable<FeedRecord> FetchAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct) =>
        futuresClient.FetchTopPositionRatioAsync(symbol, interval, fromMs, toMs, ct);
}
