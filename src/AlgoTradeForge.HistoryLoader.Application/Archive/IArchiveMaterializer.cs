using AlgoTradeForge.HistoryLoader.Application;

namespace AlgoTradeForge.HistoryLoader.Application.Archive;

/// <summary>
/// Materializes one monthly partition of one feed from a public archive.
/// Registration in <see cref="ArchiveMaterializerRegistry"/> is what makes a
/// (exchange, feed, assetType) tuple replenishable — venues without archive
/// sources (IB) are irreplaceable by construction.
/// </summary>
public interface IArchiveMaterializer
{
    string Exchange { get; }
    string FeedName { get; }
    bool Supports(string assetType);
    Task<ArchiveMonthResult> MaterializeMonth(
        AssetCollectionConfig assetConfig,
        FeedCollectionConfig feedConfig,
        string assetDir,
        int year, int month,
        CancellationToken ct = default);
}

public readonly record struct ArchiveMonthResult(long RowsWritten, bool AvailableAtSource);
