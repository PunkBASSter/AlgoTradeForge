using System.Text.Json;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Scoring;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Application.Validation;

/// <summary>
/// Computes a weighted composite score (0–100) across 7 validation categories,
/// applies hard rejection rules, and returns a traffic-light verdict.
/// Pure function — no I/O, no DI.
/// </summary>
public static class CompositeScoreCalculator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Category weights (sum = 1.0). WFO + WFM = 0.25 combined per PRD.
    private static readonly (string Key, double Weight)[] CategoryWeights =
    [
        (CompositeScoreResult.CategoryData, 0.05),
        (CompositeScoreResult.CategoryStats, 0.20),
        (CompositeScoreResult.CategoryParams, 0.15),
        (CompositeScoreResult.CategoryWfo, 0.15),
        (CompositeScoreResult.CategoryWfm, 0.10),
        (CompositeScoreResult.CategoryMc, 0.15),
        (CompositeScoreResult.CategorySubPeriod, 0.10),
    ];

    /// <summary>
    /// Computes the composite validation score from stage results.
    /// </summary>
    public static CompositeScoreResult Calculate(
        IReadOnlyList<StageResultRecord> stageResults,
        ValidationThresholdProfile profile,
        int candidatesIn,
        int candidatesOut)
    {
        var stageMap = IndexStageResults(stageResults);
        var rejections = CheckHardRejections(stageMap, profile, candidatesOut);
        var categoryScores = ComputeCategoryScores(stageMap);

        var compositeScore = ComputeWeightedScore(categoryScores, stageMap);
        compositeScore = Math.Round(compositeScore, 1);

        var verdict = DetermineVerdict(compositeScore, rejections, candidatesOut);
        var summary = BuildSummary(compositeScore, verdict, rejections, candidatesIn, candidatesOut, stageResults);

        return new CompositeScoreResult(compositeScore, verdict, summary, rejections, categoryScores);
    }

    // --- Stage indexing ---

    private static Dictionary<int, List<CandidateVerdict>> IndexStageResults(
        IReadOnlyList<StageResultRecord> stageResults)
    {
        var map = new Dictionary<int, List<CandidateVerdict>>();
        foreach (var sr in stageResults)
        {
            if (string.IsNullOrEmpty(sr.CandidateVerdictsJson)) continue;
            var verdicts = JsonSerializer.Deserialize<List<CandidateVerdict>>(sr.CandidateVerdictsJson, JsonOptions);
            if (verdicts is { Count: > 0 })
                map[sr.StageNumber] = verdicts;
        }
        return map;
    }

    // --- Hard rejection rules ---

    private static List<string> CheckHardRejections(
        Dictionary<int, List<CandidateVerdict>> stageMap,
        ValidationThresholdProfile profile,
        int candidatesOut)
    {
        var rejections = new List<string>();

        if (candidatesOut == 0)
            rejections.Add("NO_SURVIVORS");

        // Safety floor: trade count < MinTradeCount
        if (TryGetMedianMetric(stageMap, 1, "tradeCount", out var tradeCount)
            && tradeCount < profile.SafetyFloors.MinTradeCount)
            rejections.Add("SAFETY_FLOOR_TRADES");

        // Safety floor: WFE < MinWfe
        if (TryGetFirstMetric(stageMap, 4, "wfe", out var wfe)
            && wfe < profile.SafetyFloors.MinWfe)
            rejections.Add("SAFETY_FLOOR_WFE");

        // Safety floor: PBO > MaxPbo
        if (TryGetFirstMetric(stageMap, 7, "pbo", out var pbo)
            && pbo > profile.SafetyFloors.MaxPbo)
            rejections.Add("SAFETY_FLOOR_PBO");

        // Cost stress: unprofitable
        if (TryGetMedianMetric(stageMap, 6, "costStressNetProfit", out var costStress)
            && costStress <= 0)
            rejections.Add("COST_STRESS_UNPROFITABLE");

        return rejections;
    }

    // --- Category scoring ---

    private static Dictionary<string, double> ComputeCategoryScores(
        Dictionary<int, List<CandidateVerdict>> stageMap)
    {
        var scores = new Dictionary<string, double>();

        scores[CompositeScoreResult.CategoryData] = ScoreData(stageMap);
        scores[CompositeScoreResult.CategoryStats] = ScoreStats(stageMap);
        scores[CompositeScoreResult.CategoryParams] = ScoreParams(stageMap);
        scores[CompositeScoreResult.CategoryWfo] = ScoreWfo(stageMap);
        scores[CompositeScoreResult.CategoryWfm] = ScoreWfm(stageMap);
        scores[CompositeScoreResult.CategoryMc] = ScoreMc(stageMap);
        scores[CompositeScoreResult.CategorySubPeriod] = ScoreSubPeriod(stageMap);

        return scores;
    }

    private static double ScoreData(Dictionary<int, List<CandidateVerdict>> stageMap)
    {
        if (!stageMap.ContainsKey(1)) return 0.0;
        var scores = new List<double>();
        if (TryGetMedianMetric(stageMap, 1, "tradeCount", out var tc))
            scores.Add(MetricNormalizer.Normalize(tc, floor: 30, excellent: 300));
        if (TryGetMedianMetric(stageMap, 1, "profitFactor", out var pf))
            scores.Add(MetricNormalizer.Normalize(pf, floor: 1.0, excellent: 3.0));
        return scores.Count > 0 ? scores.Average() : 0.0;
    }

    private static double ScoreStats(Dictionary<int, List<CandidateVerdict>> stageMap)
    {
        if (!stageMap.ContainsKey(2)) return 0.0;
        var scores = new List<double>();
        if (TryGetMedianMetric(stageMap, 2, "psr", out var psr))
            scores.Add(MetricNormalizer.Normalize(psr, floor: 0.5, excellent: 1.0));
        if (TryGetMedianMetric(stageMap, 2, "sharpe", out var sharpe))
            scores.Add(MetricNormalizer.Normalize(sharpe, floor: 0.0, excellent: 2.5));
        if (TryGetMedianMetric(stageMap, 2, "recoveryFactor", out var rf))
            scores.Add(MetricNormalizer.Normalize(rf, floor: 0.5, excellent: 5.0));
        return scores.Count > 0 ? scores.Average() : 0.0;
    }

    private static double ScoreParams(Dictionary<int, List<CandidateVerdict>> stageMap)
    {
        if (!stageMap.ContainsKey(3)) return 0.0;
        var scores = new List<double>();
        if (TryGetMedianMetric(stageMap, 3, "meanFitnessRetention", out var ret))
            scores.Add(MetricNormalizer.Normalize(ret, floor: 0.3, excellent: 1.0));
        if (TryGetMedianMetric(stageMap, 3, "primaryClusterConcentration", out var cc))
            scores.Add(MetricNormalizer.Normalize(cc, floor: 0.3, excellent: 0.9));
        return scores.Count > 0 ? scores.Average() : 0.0;
    }

    private static double ScoreWfo(Dictionary<int, List<CandidateVerdict>> stageMap)
    {
        if (!stageMap.ContainsKey(4)) return 0.0;
        var scores = new List<double>();
        if (TryGetFirstMetric(stageMap, 4, "wfe", out var wfe))
            scores.Add(MetricNormalizer.Normalize(wfe, floor: 0.0, excellent: 1.0));
        if (TryGetFirstMetric(stageMap, 4, "profitableWindowsPct", out var pw))
            scores.Add(MetricNormalizer.Normalize(pw, floor: 0.3, excellent: 1.0));
        return scores.Count > 0 ? scores.Average() : 0.0;
    }

    private static double ScoreWfm(Dictionary<int, List<CandidateVerdict>> stageMap)
    {
        if (!stageMap.ContainsKey(5)) return 0.0;
        if (!TryGetFirstMetric(stageMap, 5, "clusterPassCount", out var pass)) return 0.0;
        if (!TryGetFirstMetric(stageMap, 5, "totalCells", out var total) || total <= 0) return 0.0;
        return MetricNormalizer.Normalize(pass / total, floor: 0.0, excellent: 1.0);
    }

    private static double ScoreMc(Dictionary<int, List<CandidateVerdict>> stageMap)
    {
        if (!stageMap.ContainsKey(6)) return 0.0;
        var scores = new List<double>();
        if (TryGetMedianMetric(stageMap, 6, "ddMultiplier", out var dd))
            scores.Add(MetricNormalizer.NormalizeInverted(dd, floor: 3.0, excellent: 1.0));
        if (TryGetMedianMetric(stageMap, 6, "permutationPValue", out var pv))
            scores.Add(MetricNormalizer.NormalizeInverted(pv, floor: 0.5, excellent: 0.001));
        if (TryGetMedianMetric(stageMap, 6, "probabilityOfRuin", out var por))
            scores.Add(MetricNormalizer.NormalizeInverted(por, floor: 0.5, excellent: 0.0));
        return scores.Count > 0 ? scores.Average() : 0.0;
    }

    private static double ScoreSubPeriod(Dictionary<int, List<CandidateVerdict>> stageMap)
    {
        if (!stageMap.ContainsKey(7)) return 0.0;
        var scores = new List<double>();
        if (TryGetMedianMetric(stageMap, 7, "profitableSubPeriodsPct", out var sp))
            scores.Add(MetricNormalizer.Normalize(sp, floor: 0.3, excellent: 1.0));
        if (TryGetMedianMetric(stageMap, 7, "equityCurveR2", out var r2))
            scores.Add(MetricNormalizer.Normalize(r2, floor: 0.5, excellent: 0.99));
        if (TryGetMedianMetric(stageMap, 7, "sharpeDecaySlope", out var slope))
            scores.Add(MetricNormalizer.Normalize(slope, floor: -0.01, excellent: 0.0));
        return scores.Count > 0 ? scores.Average() : 0.0;
    }

    // --- Weighted aggregation with weight redistribution ---

    private static double ComputeWeightedScore(
        Dictionary<string, double> categoryScores,
        Dictionary<int, List<CandidateVerdict>> stageMap)
    {
        // Map category → stage number for presence detection
        var categoryStageMap = new Dictionary<string, int>
        {
            [CompositeScoreResult.CategoryData] = 1,
            [CompositeScoreResult.CategoryStats] = 2,
            [CompositeScoreResult.CategoryParams] = 3,
            [CompositeScoreResult.CategoryWfo] = 4,
            [CompositeScoreResult.CategoryWfm] = 5,
            [CompositeScoreResult.CategoryMc] = 6,
            [CompositeScoreResult.CategorySubPeriod] = 7,
        };

        // Calculate total weight of present categories for redistribution
        double presentWeight = 0;
        foreach (var (key, weight) in CategoryWeights)
        {
            if (categoryStageMap.TryGetValue(key, out var stageNum) && stageMap.ContainsKey(stageNum))
                presentWeight += weight;
        }

        if (presentWeight <= 0) return 0.0;

        double weighted = 0;
        foreach (var (key, weight) in CategoryWeights)
        {
            if (!categoryScores.TryGetValue(key, out var score)) continue;
            if (!categoryStageMap.TryGetValue(key, out var stageNum) || !stageMap.ContainsKey(stageNum))
                continue;

            // Redistribute: this category's effective weight = original / totalPresent * 100%
            var effectiveWeight = weight / presentWeight;
            weighted += score * effectiveWeight;
        }

        return weighted;
    }

    // --- Verdict determination ---

    private static string DetermineVerdict(double compositeScore, List<string> rejections, int candidatesOut)
    {
        if (rejections.Count > 0) return CompositeScoreResult.VerdictRed;
        if (candidatesOut == 0) return CompositeScoreResult.VerdictRed;
        if (compositeScore >= 70.0) return CompositeScoreResult.VerdictGreen;
        if (compositeScore >= 40.0) return CompositeScoreResult.VerdictYellow;
        return CompositeScoreResult.VerdictRed;
    }

    private static string BuildSummary(
        double score, string verdict, List<string> rejections,
        int candidatesIn, int candidatesOut,
        IReadOnlyList<StageResultRecord> stageResults)
    {
        var scoreStr = $"{score:F0}/100";
        var survivorStr = $"{candidatesOut}/{candidatesIn} candidates survived";

        if (rejections.Count > 0 && rejections[0] == "NO_SURVIVORS")
        {
            var lastStage = stageResults.Count > 0 ? stageResults[^1].StageName : "unknown";
            return $"Strategy FAILS — no candidates survived pipeline (last stage: {lastStage}).";
        }

        if (rejections.Count > 0)
        {
            var reasons = string.Join(", ", rejections);
            return $"Strategy FAILS validation — {reasons}. Score: {scoreStr}. {survivorStr}.";
        }

        return verdict switch
        {
            CompositeScoreResult.VerdictGreen =>
                $"Strategy PASSES validation at {scoreStr} — {survivorStr} all stages.",
            CompositeScoreResult.VerdictYellow =>
                $"Strategy MARGINAL at {scoreStr} — review recommended. {survivorStr}.",
            _ =>
                $"Strategy FAILS at {scoreStr} — insufficient robustness across validation criteria. {survivorStr}.",
        };
    }

    // --- Metric extraction helpers ---

    /// <summary>
    /// Gets the median value of a metric across all verdicts in a gate-level or per-candidate stage.
    /// For gate-level stages (4, 5, 7-gate), all verdicts have the same value so median == value.
    /// </summary>
    private static bool TryGetMedianMetric(
        Dictionary<int, List<CandidateVerdict>> stageMap,
        int stageNumber, string metricKey, out double value)
    {
        value = 0.0;
        if (!stageMap.TryGetValue(stageNumber, out var verdicts)) return false;

        var values = new List<double>();
        foreach (var v in verdicts)
        {
            if (v.Metrics.TryGetValue(metricKey, out var m) && !double.IsNaN(m))
                values.Add(m);
        }

        if (values.Count == 0) return false;
        values.Sort();
        value = values[values.Count / 2]; // median (lower-middle for even count)
        return true;
    }

    /// <summary>
    /// Gets a metric from the first verdict of a stage. Used for gate-level stages
    /// where all verdicts share the same value.
    /// </summary>
    private static bool TryGetFirstMetric(
        Dictionary<int, List<CandidateVerdict>> stageMap,
        int stageNumber, string metricKey, out double value)
    {
        value = 0.0;
        if (!stageMap.TryGetValue(stageNumber, out var verdicts) || verdicts.Count == 0) return false;
        return verdicts[0].Metrics.TryGetValue(metricKey, out value);
    }
}
