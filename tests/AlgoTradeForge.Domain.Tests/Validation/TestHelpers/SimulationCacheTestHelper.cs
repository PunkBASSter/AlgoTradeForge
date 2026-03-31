namespace AlgoTradeForge.Domain.Tests.Validation.TestHelpers;

/// <summary>
/// Shared helper for creating SimulationCache test data with uniform timestamps across trials.
/// </summary>
internal static class SimulationCacheTestHelper
{
    /// <summary>
    /// Creates a jagged timestamp array by cloning the template for each trial.
    /// Used when all trials share the same timestamp sequence.
    /// </summary>
    internal static long[][] ReplicateTimestamps(long[] template, int trialCount) =>
        Enumerable.Range(0, trialCount).Select(_ => (long[])template.Clone()).ToArray();
}
