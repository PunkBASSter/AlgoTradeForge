namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public interface IJobCancellationMap
{
    CancellationToken Register(string jobId, CancellationToken linkedTo);
    void Trip(string jobId);
    void Remove(string jobId);
}
