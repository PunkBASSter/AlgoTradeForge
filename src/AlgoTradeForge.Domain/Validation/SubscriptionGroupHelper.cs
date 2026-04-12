namespace AlgoTradeForge.Domain.Validation;

/// <summary>
/// Shared utilities for partitioning trials by subscription group.
/// Used by validation stages that need subscription-aware analysis.
/// </summary>
public static class SubscriptionGroupHelper
{
    private const string AllGroupKey = "_all";

    /// <summary>
    /// Partitions trial indices into groups by subscription key.
    /// If no grouping is defined, returns a single group with all indices.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<int>> PartitionIndices(
        IReadOnlyList<int> indices,
        IReadOnlyDictionary<int, string>? groupMap)
    {
        if (groupMap is null)
            return new Dictionary<string, IReadOnlyList<int>> { [AllGroupKey] = indices };

        var result = new Dictionary<string, List<int>>();
        foreach (var idx in indices)
        {
            var key = groupMap.TryGetValue(idx, out var k) ? k : AllGroupKey;
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }

            list.Add(idx);
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);
    }

    /// <summary>
    /// Returns true if there is exactly one subscription group (no partitioning needed).
    /// </summary>
    public static bool IsSingleGroup(IReadOnlyDictionary<int, string>? groupMap)
        => groupMap is null || !groupMap.Values.Distinct().Skip(1).Any();

    /// <summary>
    /// Returns the set of trial indices belonging to a specific subscription group.
    /// When no group map exists, returns all trial indices.
    /// </summary>
    public static HashSet<int> GetTrialIndicesForGroup(
        IReadOnlyDictionary<int, string>? groupMap,
        string groupKey,
        int totalTrials)
    {
        if (groupMap is null)
            return new HashSet<int>(Enumerable.Range(0, totalTrials));

        var set = new HashSet<int>();
        foreach (var (idx, key) in groupMap)
        {
            if (key == groupKey)
                set.Add(idx);
        }

        return set;
    }
}
