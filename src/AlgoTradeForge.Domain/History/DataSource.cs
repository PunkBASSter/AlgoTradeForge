namespace AlgoTradeForge.Domain.History;

public interface IDataSource
{
    Task<TimeSeries<Int64Bar>> GetData(HistoryDataQuery query, CancellationToken ct = default);
}
