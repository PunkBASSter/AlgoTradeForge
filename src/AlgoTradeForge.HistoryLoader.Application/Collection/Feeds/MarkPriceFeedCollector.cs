using System.Runtime.CompilerServices;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class MarkPriceFeedCollector(
    IServiceProvider serviceProvider,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<MarkPriceFeedCollector> logger)
    : GenericFeedCollectorBase(serviceProvider, feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.MarkPrice;
    protected override string[] Columns => ["o", "h", "l", "c"];

    protected override async IAsyncEnumerable<FeedRecord> FetchAsync(
        AssetCollectionConfig assetConfig,
        string symbol, string interval, long fromMs, long toMs,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var key = ExchangeKeys.Resolve(assetConfig);
        var fetcher = ServiceProvider.GetRequiredKeyedService<IMarkPriceCandleFetcher>(key);

        await foreach (var kline in fetcher
            .FetchMarkPriceCandlesAsync(symbol, interval, fromMs, toMs, ct)
            .WithCancellation(ct))
        {
            yield return new FeedRecord(kline.TimestampMs,
            [
                (double)kline.Open,
                (double)kline.High,
                (double)kline.Low,
                (double)kline.Close
            ]);
        }
    }
}
