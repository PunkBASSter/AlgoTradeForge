using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class HistoryRepository(
    IInt64BarLoader barLoader,
    HistoryFeedResolverFactory resolverFactory,
    IOptions<CandleStorageOptions> storageOptions) : IHistoryRepository
{
    //TODO: investigate if upcast is required
    public Task<TimeSeries<Int64Bar>> Load(DataFeedSubscription subscription, DateOnly from, DateOnly to, CancellationToken ct = default)
        => LoadTimeBar(subscription.RequireAsset(), ((TimeBarSubscription)subscription).TimeFrame, from, to, ct);

    public Task<TimeSeries<Int64Bar>> Load(Asset asset, DataFeedSubscription subscription, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var dataRoot = storageOptions.Value.DataRoot;
        var assetDir = AssetDirectoryName.From(asset);

        return subscription switch
        {
            TimeBarSubscription tb => LoadTimeBar(asset, tb.TimeFrame, from, to, ct),
            AltBarSubscription ab => barLoader.Load(
                new DataFeedDescriptor(dataRoot, asset.Exchange, assetDir, ab.FeedId, DataFeedKind.AltBar),
                from, to, ct),
            TickSubscription => barLoader.Load(
                new DataFeedDescriptor(dataRoot, asset.Exchange, assetDir, "ticks", DataFeedKind.Tick),
                from, to, ct),
            SideFeedSubscription => throw new ArgumentException(
                "Side feeds cannot be loaded as a primary OHLCV series. " +
                "Side feeds are FeedSeries, not TimeSeries<Int64Bar> — bind them via IFeedContext / FeedContextBuilder.",
                nameof(subscription)),
            _ => throw new ArgumentOutOfRangeException(nameof(subscription),
                $"Unknown DataFeedSubscription subtype: {subscription.GetType().Name}"),
        };
    }

    private async Task<TimeSeries<Int64Bar>> LoadTimeBar(Asset asset, TimeFrame timeFrame, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var resolution = await resolverFactory.For(asset).Resolve(asset, timeFrame, ct);

        var descriptor = new DataFeedDescriptor(
            storageOptions.Value.DataRoot, asset.Exchange, AssetDirectoryName.From(asset),
            resolution.LoadFeedId, DataFeedKind.TimeBar);
        var raw = await barLoader.Load(descriptor, from, to, ct);

        return resolution.Resample ? raw.Resample(timeFrame) : raw;
    }
}
