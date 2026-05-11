using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Application.CandleIngestion;

/// <summary>
/// Loads <see cref="Int64Bar"/> series from partitioned CSV storage. Path resolution is
/// driven by <see cref="DataFeedDescriptor"/>; supports time bars, alt bars, ticks, and side feeds.
/// </summary>
public interface IInt64BarLoader
{
    TimeSeries<Int64Bar> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to);

    DateTimeOffset? GetLastTimestamp(DataFeedDescriptor feed);
}
