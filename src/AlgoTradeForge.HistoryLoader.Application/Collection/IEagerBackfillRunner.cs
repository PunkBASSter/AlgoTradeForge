namespace AlgoTradeForge.HistoryLoader.Application.Collection;

/// <summary>Kick seam over <see cref="BackfillOrchestrator"/> — one asset, a feed-name filter.
/// Lets the reconciler pipeline fire eager backfills without depending on the concrete orchestrator
/// (and lets tests substitute the kick).</summary>
public interface IEagerBackfillRunner
{
    Task Run(CollectionAsset asset, IReadOnlyList<string> feeds, CancellationToken ct = default);
}
