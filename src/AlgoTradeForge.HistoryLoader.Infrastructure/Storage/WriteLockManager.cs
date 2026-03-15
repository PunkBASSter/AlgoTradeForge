using System.Collections.Concurrent;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// Provides per-key mutual exclusion for CSV writers so that scheduled collectors
/// and backfill cannot write to the same feed file simultaneously.
/// </summary>
internal sealed class WriteLockManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public SemaphoreSlim GetLock(string key) =>
        _locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
}
