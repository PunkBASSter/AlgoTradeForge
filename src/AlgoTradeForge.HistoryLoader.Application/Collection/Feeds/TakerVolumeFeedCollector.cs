using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class TakerVolumeFeedCollector(
    IServiceProvider serviceProvider,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<TakerVolumeFeedCollector> logger)
    : GenericFeedCollectorBase(serviceProvider, feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.TakerVolume;
    protected override string[] Columns => ["buy_vol_usd", "sell_vol_usd", "ratio"];

    protected override IAsyncEnumerable<FeedRecord> FetchAsync(
        AssetCollectionConfig assetConfig,
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct)
    {
        var key = ExchangeKeys.Resolve(assetConfig);
        var fetcher = ServiceProvider.GetRequiredKeyedService<ITakerVolumeFetcher>(key);
        return fetcher.FetchTakerVolumeAsync(symbol, interval, fromMs, toMs, ct);
    }
}
