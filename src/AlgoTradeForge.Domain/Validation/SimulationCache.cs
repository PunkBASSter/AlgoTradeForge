namespace AlgoTradeForge.Domain.Validation;

/// <summary>
/// Stores per-trial P&amp;L deltas and timestamps from optimization trials.
/// Supports variable-length equity curves across trials (e.g. when using SubscriptionAxis
/// with different assets that have different bar counts).
/// </summary>
public sealed class SimulationCache
{
    /// <summary>Per-trial timestamps (variable length). <c>TrialTimestamps[i].Length == TrialPnlMatrix[i].Length</c>.</summary>
    public long[][] TrialTimestamps { get; }

    /// <summary>Per-trial P&amp;L deltas (variable length).</summary>
    public double[][] TrialPnlMatrix { get; }

    public int TrialCount { get; }

    /// <summary>Bar count of the longest trial.</summary>
    public int MaxBarCount { get; }

    /// <summary>Global minimum timestamp across all trials.</summary>
    public long MinTimestamp { get; }

    /// <summary>Global maximum timestamp across all trials.</summary>
    public long MaxTimestamp { get; }

    public SimulationCache(long[][] trialTimestamps, double[][] trialPnlMatrix)
    {
        ArgumentNullException.ThrowIfNull(trialTimestamps);
        ArgumentNullException.ThrowIfNull(trialPnlMatrix);

        if (trialTimestamps.Length != trialPnlMatrix.Length)
            throw new ArgumentException(
                $"Timestamp arrays ({trialTimestamps.Length}) and PnL arrays ({trialPnlMatrix.Length}) must have the same count.");

        var maxBars = 0;
        var minTs = long.MaxValue;
        var maxTs = long.MinValue;

        for (var i = 0; i < trialPnlMatrix.Length; i++)
        {
            if (trialTimestamps[i].Length != trialPnlMatrix[i].Length)
                throw new ArgumentException(
                    $"Trial {i} has {trialTimestamps[i].Length} timestamps but {trialPnlMatrix[i].Length} PnL values.");

            var len = trialPnlMatrix[i].Length;
            if (len > maxBars) maxBars = len;

            if (len > 0)
            {
                if (trialTimestamps[i][0] < minTs) minTs = trialTimestamps[i][0];
                if (trialTimestamps[i][^1] > maxTs) maxTs = trialTimestamps[i][^1];
            }
        }

        TrialTimestamps = trialTimestamps;
        TrialPnlMatrix = trialPnlMatrix;
        TrialCount = trialPnlMatrix.Length;
        MaxBarCount = maxBars;
        MinTimestamp = trialPnlMatrix.Length > 0 && maxBars > 0 ? minTs : 0;
        MaxTimestamp = trialPnlMatrix.Length > 0 && maxBars > 0 ? maxTs : 0;
    }

    /// <summary>Returns the bar count for a specific trial.</summary>
    public int GetBarCount(int trialIndex) => TrialPnlMatrix[trialIndex].Length;

    /// <summary>Returns the timestamps for a specific trial.</summary>
    public ReadOnlySpan<long> GetTrialTimestamps(int trialIndex) => TrialTimestamps[trialIndex];

    /// <summary>Returns the P&amp;L row for a single trial as a span (zero-allocation).</summary>
    public ReadOnlySpan<double> GetTrialPnl(int trialIndex) => TrialPnlMatrix[trialIndex];

    /// <summary>Returns a sub-window of a trial's P&amp;L as a span (zero-allocation, zero-copy).</summary>
    public ReadOnlySpan<double> GetTrialPnlWindow(int trialIndex, int startBar, int length) =>
        TrialPnlMatrix[trialIndex].AsSpan(startBar, length);

    /// <summary>
    /// Finds the bar index range within a trial that falls in the timestamp range [startTsInclusive, endTsExclusive).
    /// Uses binary search on the trial's sorted timestamps.
    /// </summary>
    /// <returns>Tuple of (startBarIndex, length). Length may be 0 if no bars fall in the range.</returns>
    public (int start, int length) FindTrialWindow(int trialIndex, long startTsInclusive, long endTsExclusive)
    {
        var timestamps = TrialTimestamps[trialIndex];
        if (timestamps.Length == 0) return (0, 0);

        // Find first bar >= startTsInclusive
        var lo = LowerBound(timestamps, startTsInclusive);
        // Find first bar >= endTsExclusive (exclusive end)
        var hi = LowerBound(timestamps, endTsExclusive);

        return (lo, hi - lo);
    }

    /// <summary>Computes cumulative equity curve for a trial: running sum of P&amp;L deltas + initial equity.</summary>
    public double[] ComputeCumulativeEquity(int trialIndex, double initialEquity)
    {
        var pnl = TrialPnlMatrix[trialIndex];
        var equity = new double[pnl.Length];
        var cumulative = initialEquity;
        for (var i = 0; i < pnl.Length; i++)
        {
            cumulative += pnl[i];
            equity[i] = cumulative;
        }

        return equity;
    }

    /// <summary>Returns the index of the first element >= value (standard lower_bound).</summary>
    private static int LowerBound(long[] sorted, long value)
    {
        var lo = 0;
        var hi = sorted.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (sorted[mid] < value)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }
}
