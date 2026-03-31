using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Builds a <see cref="SimulationCache"/> from optimization trial records.
/// Groups trials by <see cref="DataSubscriptionDto"/> so that trials sharing the same
/// asset/exchange/timeframe share a single timeline (deduplicated timestamps).
/// </summary>
public static class SimulationCacheBuilder
{
    /// <summary>Estimates the in-memory size of a cache built from the given trials.</summary>
    public static long EstimateSize(IReadOnlyList<BacktestRunRecord> trials)
    {
        if (trials.Count == 0) return 0;

        // Group by (subscription, barCount) to count unique timelines for timestamp estimate.
        var seen = new HashSet<(DataSubscriptionDto, int)>();
        var totalBars = 0L;
        var uniqueTimelineBars = 0L;

        foreach (var trial in trials)
        {
            var bars = trial.EquityCurve.Count;
            totalBars += bars;
            if (seen.Add((trial.DataSubscription, bars)))
                uniqueTimelineBars += bars;
        }

        return totalBars * sizeof(double)            // PnL matrix (per trial)
             + uniqueTimelineBars * sizeof(long);     // timestamps (per unique timeline)
    }

    public static SimulationCache Build(IReadOnlyList<BacktestRunRecord> trials)
    {
        if (trials.Count == 0)
            throw new ArgumentException("No trials provided.");

        if (trials[0].EquityCurve.Count == 0)
            throw new ArgumentException("Trial 0 has an empty equity curve.");

        // Group trials by (DataSubscription, BarCount) → one timeline per group.
        // Same subscription but different bar counts (e.g., early-stopped trials) get separate timelines.
        var timelineKeys = new Dictionary<(DataSubscriptionDto Sub, int BarCount), int>();
        var timelines = new List<long[]>();
        var trialTimelineIndex = new int[trials.Count];
        var matrix = new double[trials.Count][];

        for (var t = 0; t < trials.Count; t++)
        {
            var key = (trials[t].DataSubscription, trials[t].EquityCurve.Count);
            if (!timelineKeys.TryGetValue(key, out var tlIdx))
            {
                // First trial for this (subscription, barCount) — extract timestamps as the timeline
                tlIdx = timelines.Count;
                timelineKeys[key] = tlIdx;
                var curve = trials[t].EquityCurve;
                var ts = new long[curve.Count];
                for (var i = 0; i < curve.Count; i++)
                    ts[i] = curve[i].TimestampMs;
                timelines.Add(ts);
            }

            trialTimelineIndex[t] = tlIdx;

            // Build PnL deltas
            var ec = trials[t].EquityCurve;
            var deltas = new double[ec.Count];
            if (ec.Count > 0)
            {
                deltas[0] = ec[0].Value - (double)trials[t].Metrics.InitialCapital;
                for (var i = 1; i < ec.Count; i++)
                    deltas[i] = ec[i].Value - ec[i - 1].Value;
            }

            matrix[t] = deltas;
        }

        return new SimulationCache(timelines.ToArray(), trialTimelineIndex, matrix);
    }

    public static TrialSummary[] BuildTrialSummaries(IReadOnlyList<BacktestRunRecord> trials)
    {
        var summaries = new TrialSummary[trials.Count];
        for (var i = 0; i < trials.Count; i++)
        {
            summaries[i] = new TrialSummary
            {
                Index = i,
                Id = trials[i].Id,
                Metrics = trials[i].Metrics,
                Parameters = trials[i].Parameters,
            };
        }

        return summaries;
    }
}
