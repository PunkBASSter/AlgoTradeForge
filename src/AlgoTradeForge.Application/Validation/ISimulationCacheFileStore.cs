using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Validation;

namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Abstraction for persisting and loading simulation caches to/from disk.
/// </summary>
public interface ISimulationCacheFileStore
{
    /// <summary>Writes a cache to a binary file.</summary>
    void Write(SimulationCache cache, string filePath);

    /// <summary>Reads a binary cache file fully into memory.</summary>
    SimulationCache Read(string filePath);

    /// <summary>
    /// Writes trial data directly to binary format, bypassing in-memory SimulationCache
    /// construction. Used on the spillover path to avoid double-allocation.
    /// </summary>
    void WriteDirect(IReadOnlyList<BacktestRunRecord> trials, string filePath);
}
