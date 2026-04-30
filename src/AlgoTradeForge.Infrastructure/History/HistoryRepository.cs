using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class HistoryRepository(
    IInt64BarLoader barLoader,
    IOptions<CandleStorageOptions> storageOptions) : IHistoryRepository
{
    public TimeSeries<Int64Bar> Load(DataSubscription subscription, DateOnly from, DateOnly to)
    {
        var asset = subscription.Asset;
        var sourceInterval = storageOptions.Value.SourceInterval;

        if (subscription.TimeFrame < sourceInterval)
            throw new ArgumentException(
                $"Requested timeframe ({subscription.TimeFrame}) is smaller than the asset's smallest interval ({sourceInterval}).",
                nameof(subscription));

        var descriptor = new DataFeedDescriptor(
            DataRoot: storageOptions.Value.DataRoot,
            Exchange: asset.Exchange,
            Asset: AssetDirectoryName.From(subscription.Asset),
            FeedId: TimeFrameFormatter.Format(sourceInterval),
            Kind: DataFeedKind.TimeBar);

        var raw = barLoader.Load(descriptor, from, to);

        if (subscription.TimeFrame == sourceInterval)
            return raw;

        return raw.Resample(subscription.TimeFrame);
    }
}
