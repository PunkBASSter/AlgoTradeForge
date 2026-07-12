using AlgoTradeForge.HistoryLoader.Application.Archive;

namespace AlgoTradeForge.HistoryLoader.Application.Collection;

/// <summary>Testable seam over <see cref="BackfillOrchestrator.TryRunSingle"/> — lets
/// <c>ArchiveLoadService</c> operate without depending on the concrete orchestrator.</summary>
public interface IBackfillOrchestrator
{
    Task<bool> TryRunSingle(
        CollectionAsset asset,
        string assetDir,
        IReadOnlyList<string>? feedFilter = null,
        DateOnly? fromDate = null,
        DateOnly? toDate = null,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default);
}
