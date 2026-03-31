using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Builds a <see cref="SimulationCache"/> from optimization trial records.
/// Extracts per-trial timestamps and computes per-bar P&amp;L deltas from equity curves.
/// Supports variable-length equity curves across trials.
/// </summary>
public static class SimulationCacheBuilder
{
    /// <summary>Estimates the in-memory size of a cache built from the given trials.</summary>
    public static long EstimateSize(IReadOnlyList<BacktestRunRecord> trials)
    {
        if (trials.Count == 0) return 0;

        // Each trial stores its own timestamp array (variable-length, not shared).
        // This counts ~2x more than the old shared-timestamp model, but accurately
        // reflects the actual heap layout for the per-trial jagged arrays.
        var totalBars = 0L;
        foreach (var trial in trials)
            totalBars += trial.EquityCurve.Count;

        return totalBars * sizeof(double)   // PnL matrix
             + totalBars * sizeof(long);     // timestamps
    }

    public static SimulationCache Build(IReadOnlyList<BacktestRunRecord> trials)
    {
        if (trials.Count == 0)
            throw new ArgumentException("No trials provided.");

        if (trials[0].EquityCurve.Count == 0)
            throw new ArgumentException("Trial 0 has an empty equity curve.");

        var timestamps = new long[trials.Count][];
        var matrix = new double[trials.Count][];

        for (var t = 0; t < trials.Count; t++)
        {
            var curve = trials[t].EquityCurve;
            var barCount = curve.Count;

            var ts = new long[barCount];
            var deltas = new double[barCount];

            if (barCount > 0)
            {
                ts[0] = curve[0].TimestampMs;
                // delta[0] = first equity value - initial capital (captures the first bar's P&L)
                deltas[0] = curve[0].Value - (double)trials[t].Metrics.InitialCapital;

                for (var i = 1; i < barCount; i++)
                {
                    ts[i] = curve[i].TimestampMs;
                    deltas[i] = curve[i].Value - curve[i - 1].Value;
                }
            }

            timestamps[t] = ts;
            matrix[t] = deltas;
        }

        return new SimulationCache(timestamps, matrix);
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
