using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class TakerVolumeFeedCollector(
    IFuturesDataFetcher futuresClient,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<TakerVolumeFeedCollector> logger)
    : GenericFeedCollectorBase(feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.TakerVolume;
    protected override string[] Columns => ["buy_vol_usd", "sell_vol_usd", "ratio"];

    protected override IAsyncEnumerable<FeedRecord> FetchAsync(
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct) =>
        futuresClient.FetchTakerVolumeAsync(symbol, interval, fromMs, toMs, ct);
}
