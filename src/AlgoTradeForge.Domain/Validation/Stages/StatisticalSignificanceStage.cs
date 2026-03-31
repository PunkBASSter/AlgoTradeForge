using AlgoTradeForge.Domain.Validation.Statistics;

namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 2: Statistical significance. Applies Deflated Sharpe Ratio (DSR),
/// Probabilistic Sharpe Ratio (PSR), and stricter profitability thresholds.
/// </summary>
public sealed class StatisticalSignificanceStage : IValidationStage
{
    public int StageNumber => 2;
    public string StageName => "StatisticalSignificance";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.StatisticalSignificance;
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.AllCandidateIndices.Count);
        var totalTrialCount = context.Trials.Count;

        foreach (var idx in context.AllCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var trial = context.Trials[idx];
            var m = trial.Metrics;

            // Build equity curve from SimulationCache for this trial
            var initialEquity = (double)m.InitialCapital;
            var equityCurve = context.Cache.ComputeCumulativeEquity(idx, initialEquity);

            // Compute log returns and distributional moments
            var logReturns = ReturnSeriesAnalyzer.ComputeLogReturns(equityCurve);
            var (skewness, excessKurtosis) = ReturnSeriesAnalyzer.ComputeMoments(logReturns);

            var sampleSize = logReturns.Length;
            var observedSharpe = m.SharpeRatio;

            // Deflated Sharpe Ratio (corrects for multiple testing)
            var dsr = ProbabilisticSharpeRatio.ComputeDSR(
                observedSharpe, totalTrialCount, sampleSize, skewness, excessKurtosis);

            // Probabilistic Sharpe Ratio (benchmark = 0)
            var psr = ProbabilisticSharpeRatio.ComputePSR(
                observedSharpe, 0.0, sampleSize, skewness, excessKurtosis);

            // Recovery factor = NetProfit / MaxDrawdown (absolute)
            var maxDrawdown = m.MaxDrawdownPct > 0
                ? (double)m.InitialCapital * m.MaxDrawdownPct / 100.0
                : 0.0;
            var recoveryFactor = maxDrawdown > 0 ? (double)m.NetProfit / maxDrawdown : 0.0;

            var metrics = new Dictionary<string, double>
            {
                ["dsr"] = dsr,
                ["psr"] = psr,
                ["sharpe"] = observedSharpe,
                ["profitFactor"] = m.ProfitFactor,
                ["recoveryFactor"] = recoveryFactor,
                ["skewness"] = skewness,
                ["excessKurtosis"] = excessKurtosis,
            };

            string? reason = null;

            // DSR check: (1 - DSR) should be ≤ DsrPValue threshold
            if ((1.0 - dsr) > thresholds.DsrPValue)
                reason = "DSR_BELOW_THRESHOLD";
            else if (psr < thresholds.MinPsr)
                reason = "PSR_BELOW_THRESHOLD";
            else if (observedSharpe < thresholds.MinSharpe)
                reason = "SHARPE_BELOW_THRESHOLD";
            else if (m.ProfitFactor < thresholds.MinProfitFactor)
                reason = "PROFIT_FACTOR_BELOW_STAGE2";
            else if (recoveryFactor < thresholds.MinRecoveryFactor)
                reason = "RECOVERY_FACTOR_BELOW_THRESHOLD";

            var passed = reason is null;
            verdicts.Add(new CandidateVerdict(trial.Id, passed, reason, metrics));
            if (passed) survivors.Add(idx);
        }

        return new StageResult(survivors, verdicts);
    }
}
