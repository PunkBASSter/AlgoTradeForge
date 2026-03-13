using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Application.CandleIngestion;

public interface IInt64BarLoader
{
    TimeSeries<Int64Bar> Load(
        string dataRoot,
        string exchange,
        string symbol,
        DateOnly from,
        DateOnly to,
        TimeSpan interval);

    DateTimeOffset? GetLastTimestamp(string dataRoot, string exchange, string symbol);
}
