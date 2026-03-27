using System.Collections.Concurrent;
using System.Text;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Optimization;

/// <summary>
/// Thread-safe cache that memoizes fitness results by parameter key so duplicate
/// chromosomes skip backtest evaluation. Assumes deterministic backtests.
/// </summary>
public sealed class GeneticFitnessCache
{
    private readonly ConcurrentDictionary<string, CachedFitnessEntry> _store = new();
    private readonly int _maxEntries;
    private int _entryCount;
    private long _cacheHits;

    public GeneticFitnessCache(int maxEntries = 100_000)
    {
        _maxEntries = maxEntries;
    }

    /// <summary>
    /// Creates a cache if <see cref="GeneticConfig.EnableFitnessCache"/> is true; otherwise returns null.
    /// </summary>
    public static GeneticFitnessCache? Create(GeneticConfig config, int maxEntries = 100_000) =>
        config.EnableFitnessCache ? new GeneticFitnessCache(maxEntries) : null;

    /// <summary>
    /// Looks up a cached fitness entry by parameter combination.
    /// Returns the cache key via <paramref name="cacheKey"/> for subsequent <see cref="TryAdd"/>.
    /// </summary>
    public bool TryGet(ParameterCombination combo, out string cacheKey, out CachedFitnessEntry entry)
    {
        cacheKey = BuildCacheKey(combo);
        if (_store.TryGetValue(cacheKey, out entry))
        {
            Interlocked.Increment(ref _cacheHits);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Stores a fitness entry if the cache has not exceeded its capacity.
    /// Uses an atomic counter instead of <see cref="ConcurrentDictionary{TKey,TValue}.Count"/>
    /// to avoid lock contention and check-then-act races.
    /// </summary>
    public void TryAdd(string cacheKey, CachedFitnessEntry entry)
    {
        if (Interlocked.Increment(ref _entryCount) <= _maxEntries)
        {
            if (!_store.TryAdd(cacheKey, entry))
                Interlocked.Decrement(ref _entryCount); // key already existed
        }
        else
        {
            Interlocked.Decrement(ref _entryCount); // rolled back — at capacity
        }
    }

    public long ReadHits() => Interlocked.Read(ref _cacheHits);

    public int EntryCount => Volatile.Read(ref _entryCount);

    internal static string BuildCacheKey(ParameterCombination combo)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var key in combo.Values.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!first) sb.Append('|');
            first = false;
            sb.Append(key).Append('=');
            ParameterKeyBuilder.AppendValue(sb, combo.Values[key]);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Cached result of a single trial evaluation.
/// </summary>
public readonly record struct CachedFitnessEntry(
    double Fitness, bool WasFilteredOut, bool WasFailed, BacktestRunRecord? Record);
