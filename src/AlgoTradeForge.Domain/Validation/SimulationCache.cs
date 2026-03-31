namespace AlgoTradeForge.Domain.Validation;

/// <summary>
/// Per-trial data: the index into the shared <see cref="SimulationCache.Timelines"/> array
/// and the trial's own P&amp;L deltas (one per bar).
/// </summary>
public readonly record struct TrialData(int TimelineIndex, double[] PnlDeltas);

/// <summary>
/// Stores per-trial P&amp;L deltas with deduplicated timestamp timelines.
/// Trials sharing the same data subscription (asset/exchange/timeframe) share a single
/// timeline array, avoiding memory duplication and repeated binary searches.
/// </summary>
public sealed class SimulationCache
{
    /// <summary>Unique timestamp arrays, one per distinct data subscription. Typically 1 for single-asset optimizations.</summary>
    public long[][] Timelines { get; }

    /// <summary>Per-trial data bundling timeline reference and P&amp;L deltas.</summary>
    public TrialData[] Trials { get; }

    public int TrialCount => Trials.Length;

    /// <summary>Number of unique timelines.</summary>
    public int TimelineCount => Timelines.Length;

    /// <summary>Bar count of the longest trial.</summary>
    public int MaxBarCount { get; }

    /// <summary>Global minimum timestamp across all timelines.</summary>
    public long MinTimestamp { get; }

    /// <summary>Global maximum timestamp across all timelines.</summary>
    public long MaxTimestamp { get; }

    public SimulationCache(long[][] timelines, TrialData[] trials)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(trials);

        var maxBars = 0;
        var minTs = long.MaxValue;
        var maxTs = long.MinValue;

        // Compute min/max timestamps from timelines
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
        for (var t = 0; t < trials.Length; t++)
        {
            var tlIdx = trials[t].TimelineIndex;
            if (tlIdx < 0 || tlIdx >= timelines.Length)
                throw new ArgumentException(
                    $"Trial {t} has timeline index {tlIdx} but only {timelines.Length} timelines exist.");

            if (timelines[tlIdx].Length != trials[t].PnlDeltas.Length)
                throw new ArgumentException(
                    $"Trial {t} has {trials[t].PnlDeltas.Length} PnL values but its timeline has {timelines[tlIdx].Length} timestamps.");
        }

        Timelines = timelines;
        Trials = trials;
        MaxBarCount = maxBars;
        MinTimestamp = timelines.Length > 0 && maxBars > 0 ? minTs : 0;
        MaxTimestamp = timelines.Length > 0 && maxBars > 0 ? maxTs : 0;
    }

    /// <summary>Returns the timeline index for a trial.</summary>
    public int GetTimelineIndex(int trialIndex) => Trials[trialIndex].TimelineIndex;

    /// <summary>Returns the bar count for a specific trial.</summary>
    public int GetBarCount(int trialIndex) => Trials[trialIndex].PnlDeltas.Length;

    /// <summary>Returns the timestamps for a specific trial (from its shared timeline).</summary>
    public ReadOnlySpan<long> GetTrialTimestamps(int trialIndex) => Timelines[Trials[trialIndex].TimelineIndex];

    /// <summary>Returns the P&amp;L row for a single trial as a span (zero-allocation).</summary>
    public ReadOnlySpan<double> GetTrialPnl(int trialIndex) => Trials[trialIndex].PnlDeltas;

    /// <summary>Returns a sub-window of a trial's P&amp;L as a span (zero-allocation, zero-copy).</summary>
    public ReadOnlySpan<double> GetTrialPnlWindow(int trialIndex, int startBar, int length) =>
        Trials[trialIndex].PnlDeltas.AsSpan(startBar, length);

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
        FindTimelineWindow(Trials[trialIndex].TimelineIndex, startTsInclusive, endTsExclusive);

    /// <summary>Computes cumulative equity curve for a trial: running sum of P&amp;L deltas + initial equity.</summary>
    public double[] ComputeCumulativeEquity(int trialIndex, double initialEquity)
    {
        var pnl = Trials[trialIndex].PnlDeltas;
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
