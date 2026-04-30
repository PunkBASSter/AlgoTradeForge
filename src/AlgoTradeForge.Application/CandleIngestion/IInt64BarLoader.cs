using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Application.CandleIngestion;

/// <summary>
/// Loads <see cref="Int64Bar"/> series from partitioned CSV storage. The loader resolves
/// paths from <see cref="DataFeedDescriptor"/> (TRD §9.5) and supports time bars, alt bars,
/// ticks, and side feeds via <see cref="DataFeedKind"/>.
/// </summary>
public interface IInt64BarLoader
{
    TimeSeries<Int64Bar> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to);

    DateTimeOffset? GetLastTimestamp(DataFeedDescriptor feed);
}
