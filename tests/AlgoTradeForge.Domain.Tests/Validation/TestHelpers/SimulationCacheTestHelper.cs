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
    internal static SimulationCache Create(long[] timestamps, double[][] pnlMatrix)
    {
        var trials = new TrialData[pnlMatrix.Length];
        for (var t = 0; t < pnlMatrix.Length; t++)
            trials[t] = new TrialData(0, pnlMatrix[t]);
        return new SimulationCache([timestamps], trials);
    }
}
