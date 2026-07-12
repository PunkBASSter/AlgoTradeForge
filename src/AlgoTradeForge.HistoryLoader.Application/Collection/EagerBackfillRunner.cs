namespace AlgoTradeForge.HistoryLoader.Application.Collection;

public sealed class EagerBackfillRunner(BackfillOrchestrator orchestrator) : IEagerBackfillRunner
{
    public Task Run(CollectionAsset asset, IReadOnlyList<string> feeds, CancellationToken ct = default) =>
        orchestrator.Run([asset], feeds, ct: ct);
}
