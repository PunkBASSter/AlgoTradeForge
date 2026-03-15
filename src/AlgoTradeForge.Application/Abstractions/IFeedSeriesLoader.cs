using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Application.Abstractions;

/// <summary>
/// Loads auxiliary feed data for a given date range from partitioned storage.
/// </summary>
public interface IFeedSeriesLoader
{
    FeedSeries? Load(
        string dataRoot,
        string exchange,
        string assetDir,
        string feedName,
        string interval,
        DateOnly from,
        DateOnly to);
}
