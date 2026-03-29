using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Builds a <see cref="SimulationCache"/> from optimization trial records.
/// Extracts timestamps and computes per-bar P&amp;L deltas from equity curves.
/// </summary>
public static class SimulationCacheBuilder
{
    public static SimulationCache Build(IReadOnlyList<BacktestRunRecord> trials)
    {
        if (trials.Count == 0)
            throw new ArgumentException("No trials provided.");

        var firstCurve = trials[0].EquityCurve;
        if (firstCurve.Count == 0)
            throw new ArgumentException("Trial 0 has an empty equity curve.");

        var barCount = firstCurve.Count;

        // Extract shared timestamps from first trial
        var timestamps = new long[barCount];
        for (var i = 0; i < barCount; i++)
            timestamps[i] = firstCurve[i].TimestampMs;

        // Build P&L delta matrix
        var matrix = new double[trials.Count][];
        for (var t = 0; t < trials.Count; t++)
        {
            var curve = trials[t].EquityCurve;
            if (curve.Count != barCount)
                throw new ArgumentException(
                    $"Trial {t} has {curve.Count} equity points but expected {barCount}.");

            var deltas = new double[barCount];
            // delta[0] = first equity value - initial capital (captures the first bar's P&L)
            deltas[0] = curve[0].Value - (double)trials[t].Metrics.InitialCapital;
            for (var i = 1; i < barCount; i++)
                deltas[i] = curve[i].Value - curve[i - 1].Value;

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
