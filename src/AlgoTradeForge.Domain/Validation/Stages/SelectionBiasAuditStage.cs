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
        var survivors = new List<int>();
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);

        // Gate check: CSCV/PBO (runs once, applies to all)
        var pboResult = PboCalculator.Compute(context.Cache, thresholds.CscvBlocks, ct);
        var pboPassed = pboResult.Pbo <= thresholds.MaxPbo;

        var initialEquity = context.Trials.Count > 0
            ? (double)context.Trials[0].Metrics.InitialCapital
            : 10000.0;

        foreach (var idx in context.ActiveCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var trial = context.Trials[idx];
            var pnlDeltas = context.Cache.GetTrialPnl(idx);
            var metrics = new Dictionary<string, double>();

            // Gate metrics (attached to every verdict)
            metrics["pbo"] = pboResult.Pbo;
            metrics["numCombinations"] = pboResult.NumCombinations;

            if (!pboPassed)
            {
                verdicts.Add(new CandidateVerdict(trial.Id, false, "PBO_EXCESSIVE", metrics));
                continue;
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
            if (passed)
                survivors.Add(idx);

            verdicts.Add(new CandidateVerdict(trial.Id, passed, failReason, metrics));
        }

        return new StageResult(survivors, verdicts);
    }
}
