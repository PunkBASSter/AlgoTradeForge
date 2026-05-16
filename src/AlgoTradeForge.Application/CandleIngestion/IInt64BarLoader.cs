using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Application.CandleIngestion;

/// <summary>
/// Loads <see cref="Int64Bar"/> series from partitioned CSV storage. Path resolution is
/// driven by <see cref="DataFeedDescriptor"/>; supports time bars, alt bars, ticks, and side feeds.
/// </summary>
public interface IInt64BarLoader
{
    Task<TimeSeries<Int64Bar>> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to, CancellationToken ct = default);

    Task<DateTimeOffset?> GetLastTimestamp(DataFeedDescriptor feed, CancellationToken ct = default);
}
