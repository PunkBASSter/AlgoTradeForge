using AlgoTradeForge.Domain.Validation.Statistics;

namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 7: Selection bias audit. Combines a gate-level CSCV/PBO check (runs once,
/// applies to all candidates) with per-candidate sub-period consistency, regime analysis
/// (informational only), and alpha decay detection.
/// </summary>
public sealed class SelectionBiasAuditStage : IValidationStage
{
    public int StageNumber => 7;
    public string StageName => "SelectionBiasAudit";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.SelectionBiasAudit;
        var candidateCount = context.AllCandidateIndices.Count;

        // Gate check: CSCV/PBO (runs once, applies to all)
        var pboResult = PboCalculator.Compute(context.Cache, thresholds.CscvBlocks, ct);
        var pboPassed = pboResult.Pbo <= thresholds.MaxPbo;

        var initialEquity = context.Trials.Count > 0
            ? (double)context.Trials[0].Metrics.InitialCapital
            : 10000.0;

        // Per-candidate analysis is expensive — parallelize across candidates.
        // Each candidate uses read-only cache access and independent analysis, so no shared mutable state.
        var results = new (bool Passed, int Idx, CandidateVerdict Verdict)[candidateCount];

        Parallel.For(0, candidateCount, new ParallelOptions { CancellationToken = ct }, i =>
        {
            var idx = context.AllCandidateIndices[i];
            var trial = context.Trials[idx];
            var pnlDeltas = context.Cache.GetTrialPnl(idx);
            var metrics = new Dictionary<string, double>();

            // Gate metrics (attached to every verdict)
            metrics["pbo"] = pboResult.Pbo;
            metrics["numCombinations"] = pboResult.NumCombinations;

            if (!pboPassed)
            {
                results[i] = (false, idx, new CandidateVerdict(trial.Id, false, "PBO_EXCESSIVE", metrics));
                return;
            }

            string? failReason = null;

            // Check A: Sub-period consistency
            var subPeriodResult = SubPeriodAnalyzer.Analyze(
                pnlDeltas, initialEquity, thresholds.SubPeriodCount);

            metrics["profitableSubPeriodsPct"] = subPeriodResult.ProfitableSubPeriodsPct;
            metrics["equityCurveR2"] = subPeriodResult.EquityCurveR2;
            metrics["sharpeCoV"] = subPeriodResult.SharpeCoeffOfVariation;

            if (subPeriodResult.ProfitableSubPeriodsPct < thresholds.MinProfitableSubPeriods)
                failReason ??= "SUBPERIOD_INCONSISTENT";

            if (subPeriodResult.EquityCurveR2 < thresholds.MinR2)
                failReason ??= "EQUITY_CURVE_IRREGULAR";

            // Check B: Regime analysis (informational only — no hard fail)
            var regimeResult = RegimeDetector.Analyze(
                pnlDeltas, initialEquity, thresholds.RegimeVolWindow);

            metrics["regimeCount"] = regimeResult.Regimes.Count;
            metrics["profitableRegimeCount"] = regimeResult.ProfitableRegimeCount;
            metrics["sharpeRangeMin"] = regimeResult.SharpeRange.Min;
            metrics["sharpeRangeMax"] = regimeResult.SharpeRange.Max;

            // Check C: Decay analysis
            var decayResult = DecayAnalyzer.Analyze(
                pnlDeltas, initialEquity, thresholds.RollingSharpeWindow);

            metrics["sharpeDecaySlope"] = decayResult.SlopeCoefficient;
            metrics["isDecaying"] = decayResult.IsDecaying ? 1.0 : 0.0;

            if (decayResult.SlopeCoefficient < thresholds.MaxSharpeDecaySlope)
                failReason ??= "ALPHA_DECAY_DETECTED";

            var passed = failReason is null;
            results[i] = (passed, idx, new CandidateVerdict(trial.Id, passed, failReason, metrics));
        });

        // Collect results (preserves original candidate ordering)
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(candidateCount);
        foreach (var (passed, idx, verdict) in results)
        {
            if (passed) survivors.Add(idx);
            verdicts.Add(verdict);
        }

        return new StageResult(survivors, verdicts);
    }
}
