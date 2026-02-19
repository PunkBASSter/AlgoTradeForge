using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.CandleIngestion;
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
        var sourceInterval = asset.SmallestInterval;

        if (subscription.TimeFrame < sourceInterval)
            throw new ArgumentException(
                $"Requested timeframe ({subscription.TimeFrame}) is smaller than the asset's smallest interval ({sourceInterval}).",
                nameof(subscription));

        var raw = barLoader.Load(
            storageOptions.Value.DataRoot,
            asset.Exchange,
            asset.Name,
            asset.DecimalDigits,
            from,
            to,
            sourceInterval);

        if (subscription.TimeFrame == sourceInterval)
            return raw;

        return raw.Resample(subscription.TimeFrame);
    }
}
