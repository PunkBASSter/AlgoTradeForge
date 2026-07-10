namespace AlgoTradeForge.HistoryLoader.Application.Index;

public interface IIndexMaintenance
{
    void Enqueue(IndexWork work);
}
