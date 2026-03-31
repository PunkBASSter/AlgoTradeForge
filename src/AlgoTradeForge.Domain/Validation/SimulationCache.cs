namespace AlgoTradeForge.Domain.Validation;

/// <summary>
/// Stores per-trial P&amp;L deltas with deduplicated timestamp timelines.
/// Trials sharing the same data subscription (asset/exchange/timeframe) share a single
/// timeline array, avoiding memory duplication and repeated binary searches.
/// </summary>
public sealed class SimulationCache
{
    /// <summary>Unique timestamp arrays, one per distinct data subscription. Typically 1 for single-asset optimizations.</summary>
    public long[][] Timelines { get; }

    /// <summary>Maps each trial to its timeline index. <c>Timelines[TrialTimelineIndex[t]].Length == TrialPnlMatrix[t].Length</c>.</summary>
    public int[] TrialTimelineIndex { get; }

    /// <summary>Per-trial P&amp;L deltas (variable length).</summary>
    public double[][] TrialPnlMatrix { get; }

    public int TrialCount { get; }

    /// <summary>Number of unique timelines.</summary>
    public int TimelineCount { get; }

    /// <summary>Bar count of the longest trial.</summary>
    public int MaxBarCount { get; }

    /// <summary>Global minimum timestamp across all timelines.</summary>
    public long MinTimestamp { get; }

    /// <summary>Global maximum timestamp across all timelines.</summary>
    public long MaxTimestamp { get; }

    public SimulationCache(long[][] timelines, int[] trialTimelineIndex, double[][] trialPnlMatrix)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(trialTimelineIndex);
        ArgumentNullException.ThrowIfNull(trialPnlMatrix);

        if (trialTimelineIndex.Length != trialPnlMatrix.Length)
            throw new ArgumentException(
                $"Timeline index array ({trialTimelineIndex.Length}) and PnL array ({trialPnlMatrix.Length}) must have the same length.");

        var maxBars = 0;
        var minTs = long.MaxValue;
        var maxTs = long.MinValue;

        // Validate timelines and compute min/max timestamps
        for (var tl = 0; tl < timelines.Length; tl++)
        {
            var len = timelines[tl].Length;
            if (len > maxBars) maxBars = len;

            if (len > 0)
            {
                if (timelines[tl][0] < minTs) minTs = timelines[tl][0];
                if (timelines[tl][^1] > maxTs) maxTs = timelines[tl][^1];
            }
        }

        // Validate per-trial mappings
        for (var t = 0; t < trialPnlMatrix.Length; t++)
        {
            var tlIdx = trialTimelineIndex[t];
            if (tlIdx < 0 || tlIdx >= timelines.Length)
                throw new ArgumentException(
                    $"Trial {t} has timeline index {tlIdx} but only {timelines.Length} timelines exist.");

            if (timelines[tlIdx].Length != trialPnlMatrix[t].Length)
                throw new ArgumentException(
                    $"Trial {t} has {trialPnlMatrix[t].Length} PnL values but its timeline has {timelines[tlIdx].Length} timestamps.");
        }

        Timelines = timelines;
        TrialTimelineIndex = trialTimelineIndex;
        TrialPnlMatrix = trialPnlMatrix;
        TrialCount = trialPnlMatrix.Length;
        TimelineCount = timelines.Length;
        MaxBarCount = maxBars;
        MinTimestamp = timelines.Length > 0 && maxBars > 0 ? minTs : 0;
        MaxTimestamp = timelines.Length > 0 && maxBars > 0 ? maxTs : 0;
    }

    /// <summary>Returns the timeline index for a trial.</summary>
    public int GetTimelineIndex(int trialIndex) => TrialTimelineIndex[trialIndex];

    /// <summary>Returns the bar count for a specific trial.</summary>
    public int GetBarCount(int trialIndex) => TrialPnlMatrix[trialIndex].Length;

    /// <summary>Returns the timestamps for a specific trial (from its shared timeline).</summary>
    public ReadOnlySpan<long> GetTrialTimestamps(int trialIndex) => Timelines[TrialTimelineIndex[trialIndex]];

    /// <summary>Returns the P&amp;L row for a single trial as a span (zero-allocation).</summary>
    public ReadOnlySpan<double> GetTrialPnl(int trialIndex) => TrialPnlMatrix[trialIndex];

    /// <summary>Returns a sub-window of a trial's P&amp;L as a span (zero-allocation, zero-copy).</summary>
    public ReadOnlySpan<double> GetTrialPnlWindow(int trialIndex, int startBar, int length) =>
        TrialPnlMatrix[trialIndex].AsSpan(startBar, length);

    /// <summary>
    /// Finds the bar index range within a timeline that falls in [startTsInclusive, endTsExclusive).
    /// Uses binary search. All trials sharing this timeline get the same result.
    /// </summary>
    public (int start, int length) FindTimelineWindow(int timelineIndex, long startTsInclusive, long endTsExclusive)
    {
        var timestamps = Timelines[timelineIndex];
        if (timestamps.Length == 0) return (0, 0);

        var lo = LowerBound(timestamps, startTsInclusive);
        var hi = LowerBound(timestamps, endTsExclusive);

        return (lo, hi - lo);
    }

    /// <summary>
    /// Finds the bar index range within a trial's timeline that falls in [startTsInclusive, endTsExclusive).
    /// Delegates to <see cref="FindTimelineWindow"/> using the trial's timeline.
    /// </summary>
    public (int start, int length) FindTrialWindow(int trialIndex, long startTsInclusive, long endTsExclusive) =>
        FindTimelineWindow(TrialTimelineIndex[trialIndex], startTsInclusive, endTsExclusive);

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
