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
    IOptions<CandleStorageOptions> storageOptions) : IHistoryRepository
{
    public TimeSeries<Int64Bar> Load(DataSubscription subscription, DateOnly from, DateOnly to)
        => LoadTimeBar(subscription.Asset, subscription.TimeFrame, from, to);

    public TimeSeries<Int64Bar> Load(Asset asset, DataFeedSubscription subscription, DateOnly from, DateOnly to)
    {
        var dataRoot = storageOptions.Value.DataRoot;
        var assetDir = AssetDirectoryName.From(asset);

        return subscription switch
        {
            TimeBarSubscription tb => LoadTimeBar(asset, tb.TimeFrame, from, to),
            AltBarSubscription ab => barLoader.Load(
                new DataFeedDescriptor(dataRoot, asset.Exchange, assetDir, ab.FeedId, DataFeedKind.AltBar),
                from, to),
            TickSubscription => barLoader.Load(
                new DataFeedDescriptor(dataRoot, asset.Exchange, assetDir, "ticks", DataFeedKind.Tick),
                from, to),
            SideFeedSubscription => throw new ArgumentException(
                "Side feeds cannot be loaded as a primary OHLCV series. " +
                "Side feeds are FeedSeries, not TimeSeries<Int64Bar> — bind them via IFeedContext / FeedContextBuilder.",
                nameof(subscription)),
            _ => throw new ArgumentOutOfRangeException(nameof(subscription),
                $"Unknown DataFeedSubscription subtype: {subscription.GetType().Name}"),
        };
    }

    private TimeSeries<Int64Bar> LoadTimeBar(Asset asset, TimeFrame timeFrame, DateOnly from, DateOnly to)
    {
        var sourceInterval = storageOptions.Value.SourceInterval;

        if (timeFrame < sourceInterval)
            throw new ArgumentException(
                $"Requested timeframe ({timeFrame}) is smaller than the asset's smallest interval ({sourceInterval}).",
                nameof(timeFrame));

        var descriptor = new DataFeedDescriptor(
            DataRoot: storageOptions.Value.DataRoot,
            Exchange: asset.Exchange,
            Asset: AssetDirectoryName.From(asset),
            FeedId: TimeFrameFormatter.Format(sourceInterval),
            Kind: DataFeedKind.TimeBar);

        var raw = barLoader.Load(descriptor, from, to);

        return timeFrame == sourceInterval ? raw : raw.Resample(timeFrame);
    }
}
