using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class LsRatioGlobalFeedCollector(
    IServiceProvider serviceProvider,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<LsRatioGlobalFeedCollector> logger)
    : GenericFeedCollectorBase(serviceProvider, feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.LsRatioGlobal;
    protected override string[] Columns => ["long_pct", "short_pct", "ratio"];

    protected override IAsyncEnumerable<FeedRecord> FetchAsync(
        AssetCollectionConfig assetConfig,
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct)
    {
        var key = ExchangeKeys.Resolve(assetConfig);
        var fetcher = ServiceProvider.GetRequiredKeyedService<ILongShortRatioFetcher>(key);
        return fetcher.FetchGlobalLongShortRatioAsync(symbol, interval, fromMs, toMs, ct);
    }
}
