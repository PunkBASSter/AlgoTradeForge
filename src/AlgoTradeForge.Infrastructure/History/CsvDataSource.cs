using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.Infrastructure.History;

public sealed class CsvDataSource(
    IInt64BarLoader barLoader,
    IOptions<CandleStorageOptions> storageOptions) : IDataSource
{
    public TimeSeries<Int64Bar> GetData(HistoryDataQuery query)
    {
        var asset = query.Asset;
        var exchange = asset.Exchange
            ?? throw new InvalidOperationException($"Asset '{asset.Name}' has no Exchange configured.");

        var from = query.StartTime
            ?? throw new ArgumentException("StartTime is required.", nameof(query));
        var to = query.EndTime
            ?? throw new ArgumentException("EndTime is required.", nameof(query));

        var smallestInterval = asset.SmallestInterval;

        if (query.TimeFrame < smallestInterval)
            throw new ArgumentException(
                $"Requested timeframe ({query.TimeFrame}) is smaller than the asset's smallest interval ({smallestInterval}).",
                nameof(query));

        var raw = barLoader.Load(
            storageOptions.Value.DataRoot,
            exchange,
            asset.Name,
            asset.DecimalDigits,
            DateOnly.FromDateTime(from.UtcDateTime),
            DateOnly.FromDateTime(to.UtcDateTime),
            smallestInterval);

        if (query.TimeFrame > smallestInterval)
            return raw.Resample(query.TimeFrame);

        return raw;
    }
}
