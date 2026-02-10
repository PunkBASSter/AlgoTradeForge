using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Application.CandleIngestion;

public interface IInt64BarLoader
{
    TimeSeries<IntBar> Load(
        string dataRoot,
        string exchange,
        string symbol,
        int decimalDigits,
        DateOnly from,
        DateOnly to,
        TimeSpan interval);

    DateTimeOffset? GetLastTimestamp(string dataRoot, string exchange, string symbol);
}
