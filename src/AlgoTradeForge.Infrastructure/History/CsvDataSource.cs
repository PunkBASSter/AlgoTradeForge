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

        var from = query.StartTime
            ?? throw new ArgumentException("StartTime is required.", nameof(query));
        var to = query.EndTime
            ?? throw new ArgumentException("EndTime is required.", nameof(query));

        var sourceInterval = storageOptions.Value.SourceInterval;

        if (query.TimeFrame < sourceInterval)
            throw new ArgumentException(
                $"Requested timeframe ({query.TimeFrame}) is smaller than the source interval ({sourceInterval}).",
                nameof(query));

        var raw = barLoader.Load(
            storageOptions.Value.DataRoot,
            asset.Exchange,
            AssetDirectoryName.From(asset),
            DateOnly.FromDateTime(from.UtcDateTime),
            DateOnly.FromDateTime(to.UtcDateTime),
            sourceInterval);

        if (query.TimeFrame > sourceInterval)
            return raw.Resample(query.TimeFrame);

        return raw;
    }
}
