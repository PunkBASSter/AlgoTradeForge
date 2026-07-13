namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

// Seam extracted from AggregationPipeline so AggregationService can be tested without the
// full pipeline dependency graph. AggregationPipeline implements this in M3.4 when DI is wired.
public interface IAggregationPipeline
{
    Task<AggregationResult> Run(AggregationJob job, Action<ProgressEvent>? onProgress = null, CancellationToken ct = default);
}
