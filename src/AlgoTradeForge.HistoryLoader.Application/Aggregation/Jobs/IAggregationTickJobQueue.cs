namespace AlgoTradeForge.HistoryLoader.Application.Aggregation.Jobs;

/// <summary>
/// Tick-source variant of <see cref="IAggregationJobQueue"/>. Distinct DI binding lets
/// <c>AggregationWorkerHost</c> spawn a separate worker pool sized by
/// <c>aggregator.maxConcurrentTickJobs</c>, so the I/O-bound tick path doesn't head-of-line
/// the CPU-bound time-bar path.
/// </summary>
public interface IAggregationTickJobQueue : IAggregationJobQueue
{
}
