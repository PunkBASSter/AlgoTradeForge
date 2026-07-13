namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public interface IJobProgressSinkFactory
{
    IJobProgressSink For(string jobId);
}
