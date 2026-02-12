namespace AlgoTradeForge.Domain.History;

public interface IDataSource
{
    TimeSeries<Int64Bar> GetData(HistoryDataQuery query);
}
