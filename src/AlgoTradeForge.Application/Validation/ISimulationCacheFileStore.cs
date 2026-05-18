using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Validation;

namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Abstraction for persisting and loading simulation caches to/from storage.
/// </summary>
public interface ISimulationCacheFileStore
{
    /// <summary>Writes a cache to a binary file.</summary>
    Task Write(SimulationCache cache, string filePath, CancellationToken ct = default);

    /// <summary>Reads a binary cache file fully into memory.</summary>
    Task<SimulationCache> Read(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Writes trial data directly to binary format, bypassing in-memory SimulationCache
    /// construction. Used on the spillover path to avoid double-allocation.
    /// </summary>
    Task WriteDirect(IReadOnlyList<BacktestRunRecord> trials, string filePath, CancellationToken ct = default);
}
