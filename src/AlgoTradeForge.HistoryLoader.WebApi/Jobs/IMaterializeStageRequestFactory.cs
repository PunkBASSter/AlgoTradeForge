using AlgoTradeForge.HistoryLoader.Application.Jobs;

namespace AlgoTradeForge.HistoryLoader.WebApi.Jobs;

// Builds the concrete per-stage request objects a materialize run feeds to IArchiveLoadService /
// IAggregationService. The materialize row persists only the original request (exchange/symbol/
// feed/range); the source-load and derived-aggregation inputs are rebuilt from the live plan +
// options here, the same derivation the POST paths use. Kept behind an interface so the worker
// host can be unit-tested with canned requests.
internal interface IMaterializeStageRequestFactory
{
    ArchiveLoadRequest BuildLoad(MaterializePlan plan, MaterializeStage.Load stage, string jobId);
    AggregationRunRequest BuildAggregate(MaterializePlan plan, MaterializeStage.Aggregate stage, string jobId);
}
