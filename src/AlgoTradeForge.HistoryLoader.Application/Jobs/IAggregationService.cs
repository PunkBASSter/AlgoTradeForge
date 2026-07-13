using AlgoTradeForge.HistoryLoader.Application.Aggregation;

namespace AlgoTradeForge.HistoryLoader.Application.Jobs;

public sealed record AggregationRunRequest(AggregationJob Job);

public interface IAggregationService
{
    Task Run(AggregationRunRequest req, IJobProgressSink sink, CancellationToken ct = default);
}
