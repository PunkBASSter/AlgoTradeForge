using AlgoTradeForge.Domain.Validation;

namespace AlgoTradeForge.Domain.Tests.Validation.TestHelpers;

/// <summary>
/// Shared helper for creating SimulationCache test instances.
/// Most tests use a single shared timeline (all trials on the same asset).
/// </summary>
internal static class SimulationCacheTestHelper
{
    /// <summary>
    /// Creates a SimulationCache where all trials share a single timeline.
    /// This is the common case for single-asset optimizations.
    /// </summary>
    internal static SimulationCache Create(long[] timestamps, double[][] pnlMatrix) =>
        new([timestamps], AllSameTimeline(pnlMatrix.Length), pnlMatrix);

    /// <summary>
    /// Returns a timeline index array where all trials map to timeline 0.
    /// </summary>
    internal static int[] AllSameTimeline(int trialCount) => new int[trialCount];
}
