namespace AlgoTradeForge.Domain.History;

public interface IDataSource
{
    TimeSeries<IntBar> GetData(HistoryDataQuery query);
}
