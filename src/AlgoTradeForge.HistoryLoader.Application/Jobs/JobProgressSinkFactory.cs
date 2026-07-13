using AlgoTradeForge.HistoryLoader.Application.Index;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public sealed class JobProgressSinkFactory(IHistoryIndex index, IJobEventSignal signal) : IJobProgressSinkFactory
{
    public IJobProgressSink For(string jobId) => new JobProgressSink(jobId, index, signal);
}
