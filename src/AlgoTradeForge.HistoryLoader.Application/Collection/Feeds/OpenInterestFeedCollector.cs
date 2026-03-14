using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlgoTradeForge.HistoryLoader.Application.Collection.Feeds;

public sealed class OpenInterestFeedCollector(
    IServiceProvider serviceProvider,
    IFeedWriter feedWriter,
    ISchemaManager schemaManager,
    IFeedStatusStore feedStatusStore,
    ILogger<OpenInterestFeedCollector> logger)
    : GenericFeedCollectorBase(serviceProvider, feedWriter, schemaManager, feedStatusStore, logger)
{
    public override string FeedName => FeedNames.OpenInterest;
    protected override string[] Columns => ["oi", "oi_usd"];

    protected override IAsyncEnumerable<FeedRecord> FetchAsync(
        AssetCollectionConfig assetConfig,
        string symbol, string interval, long fromMs, long toMs, CancellationToken ct)
    {
        var key = ExchangeKeys.Resolve(assetConfig);
        var fetcher = ServiceProvider.GetRequiredKeyedService<IOpenInterestFetcher>(key);
        return fetcher.FetchOpenInterestAsync(symbol, interval, fromMs, toMs, ct);
    }
}
