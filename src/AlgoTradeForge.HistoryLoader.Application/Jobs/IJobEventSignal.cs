namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public interface IJobEventSignal
{
    Task Next(string jobId);   // awaitable that completes on the next Signal(jobId); lazily creates the cell
    void Signal(string jobId); // swaps in a fresh TCS and completes the previous; no-op when no cell exists
    void Evict(string jobId);  // drop the per-job cell on terminal
}
