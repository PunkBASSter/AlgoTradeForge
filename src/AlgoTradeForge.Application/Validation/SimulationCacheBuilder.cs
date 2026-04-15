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

        var hasEquityCurves = trials[0].EquityCurve.Count > 0;

        if (hasEquityCurves)
        {
            var seen = new HashSet<(DataSubscriptionDto, int)>();
            var totalBars = 0L;
            var uniqueTimelineBars = 0L;

            foreach (var trial in trials)
            {
                var bars = trial.EquityCurve.Count;
                totalBars += bars;
                if (seen.Add((trial.DataSubscriptions[0], bars)))
                    uniqueTimelineBars += bars;
            }

            return totalBars * sizeof(double) + uniqueTimelineBars * sizeof(long);
        }

        // Trade P&L path: much smaller
        var totalTrades = 0L;
        foreach (var trial in trials)
            totalTrades += trial.TradePnl.Count;

        return totalTrades * (sizeof(double) + sizeof(long));
    }

    public static SimulationCache Build(IReadOnlyList<BacktestRunRecord> trials)
    {
        if (trials.Count == 0)
            throw new ArgumentException("No trials provided.");

        // Use equity curves when available, fall back to trade P&L
        return trials[0].EquityCurve.Count > 0
            ? BuildFromEquityCurves(trials)
            : BuildFromTradePnl(trials);
    }

    private static SimulationCache BuildFromEquityCurves(IReadOnlyList<BacktestRunRecord> trials)
    {
        var timelineKeys = new Dictionary<(DataSubscriptionDto Sub, int BarCount), int>();
        var timelines = new List<long[]>();
        var trialData = new TrialData[trials.Count];

        for (var t = 0; t < trials.Count; t++)
        {
            var key = (trials[t].DataSubscriptions[0], trials[t].EquityCurve.Count);
            if (!timelineKeys.TryGetValue(key, out var tlIdx))
            {
                tlIdx = timelines.Count;
                timelineKeys[key] = tlIdx;
                var curve = trials[t].EquityCurve;
                var ts = new long[curve.Count];
                for (var i = 0; i < curve.Count; i++)
                    ts[i] = curve[i].TimestampMs;
                timelines.Add(ts);
            }

            var ec = trials[t].EquityCurve;
            var deltas = new double[ec.Count];
            if (ec.Count > 0)
            {
                deltas[0] = ec[0].Value - (double)trials[t].Metrics.InitialCapital;
                for (var i = 1; i < ec.Count; i++)
                    deltas[i] = ec[i].Value - ec[i - 1].Value;
            }

            trialData[t] = new TrialData(tlIdx, deltas);
        }

        return new SimulationCache(timelines.ToArray(), trialData);
    }

    /// <summary>
    /// Builds cache from trade-level P&amp;L when equity curves are not available.
    /// Each trial gets its own timeline (trade timestamps). The resulting cache has
    /// the same API — all validation stages work identically, just with fewer data
    /// points (~100–1000 trades vs 43K+ bars).
    /// </summary>
    private static SimulationCache BuildFromTradePnl(IReadOnlyList<BacktestRunRecord> trials)
    {
        if (trials[0].TradePnl.Count == 0)
            throw new ArgumentException("Trial 0 has neither equity curve nor trade P&L.");

        // Each trial gets its own timeline (trade timestamps are unique per trial)
        var timelines = new long[trials.Count][];
        var trialData = new TrialData[trials.Count];

        for (var t = 0; t < trials.Count; t++)
        {
            var trades = trials[t].TradePnl;
            var timestamps = new long[trades.Count];
            var pnlDeltas = new double[trades.Count];

            for (var i = 0; i < trades.Count; i++)
            {
                timestamps[i] = trades[i].TimestampMs;
                pnlDeltas[i] = trades[i].Pnl;
            }

            timelines[t] = timestamps;
            trialData[t] = new TrialData(t, pnlDeltas);
        }

        return new SimulationCache(timelines, trialData);
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

    /// <summary>
    /// Builds a trial-index-to-subscription-group-key mapping.
    /// Returns null if all trials share the same subscription (single-subscription optimization).
    /// </summary>
    public static IReadOnlyDictionary<int, string>? BuildSubscriptionGroupMap(
        IReadOnlyList<BacktestRunRecord> trials)
    {
        if (trials.Count == 0) return null;

        var map = new Dictionary<int, string>(trials.Count);
        for (var i = 0; i < trials.Count; i++)
        {
            var subs = trials[i].DataSubscriptions
                .OrderBy(s => s.AssetName).ThenBy(s => s.Exchange).ThenBy(s => s.TimeFrame);
            map[i] = string.Join(",", subs.Select(s => $"{s.AssetName}:{s.Exchange}:{s.TimeFrame}"));
        }

        var distinctGroups = new HashSet<string>(map.Values);
        return distinctGroups.Count <= 1 ? null : map;
    }
}
