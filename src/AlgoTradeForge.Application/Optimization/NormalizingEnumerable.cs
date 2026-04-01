using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Optimization;

/// <summary>
/// Wraps a lazy combination stream, applying normalization and dedup.
/// Thread-safe when consumed through <see cref="System.Collections.Concurrent.Partitioner"/>
/// with <see cref="System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering"/>,
/// which serializes MoveNext calls on the underlying enumerator.
/// </summary>
internal sealed class NormalizingEnumerable(
    IEnumerable<ParameterCombination> source,
    IParameterNormalizer normalizer)
{
    private long _skippedCount;

    /// <summary>Number of duplicate combinations skipped after normalization.</summary>
    public long SkippedCount => Interlocked.Read(ref _skippedCount);

    public IEnumerable<ParameterCombination> Enumerate()
    {
        var seen = new HashSet<string>();
        foreach (var combo in source)
        {
            var normalized = normalizer.Normalize(combo);
            var key = GeneticFitnessCache.BuildCacheKey(normalized);
            if (seen.Add(key))
                yield return normalized;
            else
                Interlocked.Increment(ref _skippedCount);
        }
    }

    /// <summary>
    /// Creates a normalizer from the strategy's params type if it implements
    /// <see cref="IParameterNormalizer"/>; otherwise returns null.
    /// </summary>
    public static IParameterNormalizer? TryCreateNormalizer(Type paramsType)
    {
        if (!typeof(IParameterNormalizer).IsAssignableFrom(paramsType))
            return null;

        try
        {
            return (IParameterNormalizer)Activator.CreateInstance(paramsType)!;
        }
        catch (MissingMethodException)
        {
            return null;
        }
    }
}
