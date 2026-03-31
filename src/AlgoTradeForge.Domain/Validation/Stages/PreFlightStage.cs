namespace AlgoTradeForge.Domain.Validation.Stages;

/// <summary>
/// Stage 0: Pre-flight checks. Validates data sufficiency and quality before
/// running expensive downstream stages.
///
/// Check 1 — MinBTL: Ensures the bar count is sufficient given the number of
///   parameter combinations tested (N), based on the expected maximum Sharpe
///   under the null from extreme value theory.
///
/// Check 2 — Data quality: Detects timestamp gaps in per-trial timestamp arrays
///   that may indicate missing data or data feed issues.
///
/// Check 3 — Cost model: Verifies that at least one trial was run with non-zero
///   commissions, catching zero-commission fantasy backtests.
/// </summary>
public sealed class PreFlightStage : IValidationStage
{
    public int StageNumber => 0;
    public string StageName => "PreFlight";

    public StageResult Execute(ValidationContext context, CancellationToken ct = default)
    {
        var thresholds = context.Profile.PreFlight;
        var cache = context.Cache;

        // --- Check 1: MinBTL (Minimum Backtest Length) ---
        // Use the minimum bar count across all trials (conservative)
        var minBarCount = int.MaxValue;
        var maxBarCount = 0;
        for (var t = 0; t < cache.TrialCount; t++)
        {
            var bc = cache.GetBarCount(t);
            if (bc < minBarCount) minBarCount = bc;
            if (bc > maxBarCount) maxBarCount = bc;
        }

        if (cache.TrialCount == 0) minBarCount = 0;

        var minBtlResult = CheckMinBtl(minBarCount, context.TotalCombinations, thresholds.MinBtlSafetyFactor);

        // --- Check 2: Data quality (timestamp gaps) ---
        // Check per-trial timestamps, report worst-case across all trials
        var worstGapResult = new GapCheckResult(true, 0, 0);
        for (var t = 0; t < cache.TrialCount; t++)
        {
            var trialGaps = CheckTimestampGaps(
                cache.TrialTimestamps[t], thresholds.MaxGapRatio, thresholds.MaxAllowedGaps);

            worstGapResult = new GapCheckResult(
                worstGapResult.Passed && trialGaps.Passed,
                Math.Max(worstGapResult.GapCount, trialGaps.GapCount),
                Math.Max(worstGapResult.LargestGapMs, trialGaps.LargestGapMs));
        }

        var gapResult = worstGapResult;

        // --- Check 3: Cost model validation ---
        var costResult = CheckCostModel(context.Trials, context.ActiveCandidateIndices, thresholds.RequireNonZeroCosts);

        // Determine if the global checks pass — these are pool-wide, not per-candidate.
        // If any global check fails, ALL candidates are rejected.
        var globalFailed = !minBtlResult.Passed || !gapResult.Passed || !costResult.Passed;

        var globalReason = !minBtlResult.Passed ? "INSUFFICIENT_DATA_LENGTH"
            : !gapResult.Passed ? "EXCESSIVE_DATA_GAPS"
            : !costResult.Passed ? "ZERO_COST_MODEL"
            : null;

        // Also check for NaN in P&L matrix (per-candidate)
        var verdicts = new List<CandidateVerdict>(context.ActiveCandidateIndices.Count);
        var survivors = new List<int>(context.ActiveCandidateIndices.Count);

        foreach (var idx in context.ActiveCandidateIndices)
        {
            ct.ThrowIfCancellationRequested();

            var trial = context.Trials[idx];
            var metrics = new Dictionary<string, double>
            {
                ["minBarCount"] = minBarCount,
                ["maxBarCount"] = maxBarCount,
                ["totalCombinations"] = context.TotalCombinations,
                ["minBtlBars"] = minBtlResult.MinBtlBars,
                ["gapCount"] = gapResult.GapCount,
                ["largestGapMs"] = gapResult.LargestGapMs,
            };

            if (globalFailed)
            {
                verdicts.Add(new CandidateVerdict(trial.Id, false, globalReason!, metrics));
                continue;
            }

            // Per-candidate: check for NaN in P&L row
            var pnl = cache.GetTrialPnl(idx);
            var hasNan = ContainsNaN(pnl);
            if (hasNan)
            {
                metrics["hasNaN"] = 1.0;
                verdicts.Add(new CandidateVerdict(trial.Id, false, "PNL_CONTAINS_NAN", metrics));
                continue;
            }

            verdicts.Add(new CandidateVerdict(trial.Id, true, null, metrics));
            survivors.Add(idx);
        }

        return new StageResult(survivors, verdicts);
    }

    /// <summary>
    /// MinBTL check using the Bailey and López de Prado expected maximum Sharpe.
    /// Formula: E[max_N] ≈ sqrt(2 * ln(N)), MinBTL ≈ N requires at least enough bars
    /// so that the expected max Sharpe from N independent trials under the null is
    /// distinguishable from the observed. Simplified: MinBTL_bars ≈ N_effective where
    /// N_effective grows slowly. We use: bars needed ≈ ceil(safetyFactor * N^0.3) as a
    /// practical heuristic that grows sub-linearly with N.
    /// For small N (≤100), almost any dataset suffices. For N=100K, need ~100+ bars.
    /// </summary>
    internal static MinBtlCheckResult CheckMinBtl(int barCount, long totalCombinations, double safetyFactor)
    {
        if (totalCombinations <= 0)
            return new MinBtlCheckResult(true, 0);

        // E[max_N] ≈ sqrt(2 * ln(N)) from extreme value theory
        // MinBTL (in bars) ≈ safetyFactor * (2 * ln(N) / E[max_N]²) * scaling
        // Since E[max_N]² = 2*ln(N), the ratio simplifies to 1.
        // A more practical formula: require bars > safetyFactor * sqrt(2 * ln(N)) * 10
        // This ensures meaningful statistical power scales with the search space.
        var n = (double)totalCombinations;
        var lnN = Math.Log(Math.Max(n, 2.0));
        var minBtlBars = (int)Math.Ceiling(safetyFactor * Math.Sqrt(2.0 * lnN) * 10.0);

        // Floor: at least 30 bars for any meaningful analysis
        minBtlBars = Math.Max(minBtlBars, 30);

        return new MinBtlCheckResult(barCount >= minBtlBars, minBtlBars);
    }

    internal readonly record struct MinBtlCheckResult(bool Passed, int MinBtlBars);

    /// <summary>
    /// Detects timestamp gaps by comparing consecutive intervals to the median interval.
    /// </summary>
    internal static GapCheckResult CheckTimestampGaps(long[] timestamps, double maxGapRatio, int maxAllowedGaps)
    {
        if (timestamps.Length < 2)
            return new GapCheckResult(true, 0, 0);

        // Compute all intervals
        var intervals = new long[timestamps.Length - 1];
        for (var i = 0; i < intervals.Length; i++)
            intervals[i] = timestamps[i + 1] - timestamps[i];

        // Compute median interval
        var sorted = (long[])intervals.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];

        if (median <= 0)
            return new GapCheckResult(true, 0, 0);

        var threshold = (long)(median * maxGapRatio);
        var gapCount = 0;
        long largestGap = 0;

        for (var i = 0; i < intervals.Length; i++)
        {
            if (intervals[i] > threshold)
            {
                gapCount++;
                if (intervals[i] > largestGap)
                    largestGap = intervals[i];
            }
        }

        return new GapCheckResult(gapCount <= maxAllowedGaps, gapCount, largestGap);
    }

    internal readonly record struct GapCheckResult(bool Passed, int GapCount, long LargestGapMs);

    /// <summary>
    /// Checks that at least one trial has non-zero total commissions.
    /// </summary>
    internal static CostModelCheckResult CheckCostModel(
        IReadOnlyList<TrialSummary> trials, IReadOnlyList<int> activeIndices, bool requireNonZeroCosts)
    {
        if (!requireNonZeroCosts)
            return new CostModelCheckResult(true);

        foreach (var idx in activeIndices)
        {
            if (trials[idx].Metrics.TotalCommissions != 0m)
                return new CostModelCheckResult(true);
        }

        return new CostModelCheckResult(false);
    }

    internal readonly record struct CostModelCheckResult(bool Passed);

    private static bool ContainsNaN(ReadOnlySpan<double> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (double.IsNaN(values[i]))
                return true;
        }

        return false;
    }
}
