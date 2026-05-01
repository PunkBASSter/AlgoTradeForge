namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// Phase 2a (P2a-10): tick-source variant of <see cref="IAggregationJobQueue"/>. Same shape;
/// distinct DI binding lets <c>AggregationWorkerHost</c> spawn a separate worker pool sized
/// by <c>aggregator.maxConcurrentTickJobs</c>. Two queues + two pools prevent the I/O-bound
/// tick path from head-of-line-blocking the CPU-bound time-bar path.
/// </summary>
public interface IAggregationTickJobQueue : IAggregationJobQueue
{
}
