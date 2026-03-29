using System.Text.Json;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Scoring;
using AlgoTradeForge.Domain.Validation.Stages;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Validation;

public sealed class CompositeScoreCalculatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly Guid TrialId = Guid.NewGuid();

    [Fact]
    public void AllStagesPresent_StrongMetrics_ReturnsGreenAbove70()
    {
        var stageResults = BuildAllStages(strong: true);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 5);

        Assert.Equal(CompositeScoreResult.VerdictGreen, result.Verdict);
        Assert.True(result.CompositeScore >= 70.0, $"Expected >= 70, got {result.CompositeScore}");
        Assert.Empty(result.Rejections);
        Assert.Equal(7, result.CategoryScores.Count);
    }

    [Fact]
    public void AllStagesPresent_WeakMetrics_ReturnsRedBelow40()
    {
        var stageResults = BuildAllStages(strong: false);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 1);

        Assert.Equal(CompositeScoreResult.VerdictRed, result.Verdict);
        Assert.True(result.CompositeScore < 40.0, $"Expected < 40, got {result.CompositeScore}");
    }

    [Fact]
    public void MarginalMetrics_ReturnsYellow()
    {
        var stageResults = BuildAllStages(strong: false, marginal: true);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 3);

        Assert.Equal(CompositeScoreResult.VerdictYellow, result.Verdict);
        Assert.InRange(result.CompositeScore, 40.0, 69.99);
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void HardRejection_PboExcessive_ForcesRed()
    {
        var stageResults = BuildAllStages(strong: true);
        // Override stage 7 with PBO > safety floor
        stageResults[7] = BuildStageResult(7, "SelectionBiasAudit", passed: true, new Dictionary<string, double>
        {
            ["pbo"] = 0.65, // above 0.60 safety floor
            ["profitableSubPeriodsPct"] = 0.90,
            ["equityCurveR2"] = 0.95,
            ["sharpeCoV"] = 0.2,
            ["sharpeDecaySlope"] = 0.0,
            ["isDecaying"] = 0,
        });
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 5);

        Assert.Equal(CompositeScoreResult.VerdictRed, result.Verdict);
        Assert.Contains("SAFETY_FLOOR_PBO", result.Rejections);
    }

    [Fact]
    public void HardRejection_LowWfe_ForcesRed()
    {
        var stageResults = BuildAllStages(strong: true);
        stageResults[4] = BuildStageResult(4, "WalkForwardOptimization", passed: true, new Dictionary<string, double>
        {
            ["wfe"] = 0.25, // below 0.30 safety floor
            ["profitableWindowsPct"] = 0.80,
            ["oosDrawdownExcessPct"] = 0.10,
            ["windowCount"] = 6,
        });
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 5);

        Assert.Equal(CompositeScoreResult.VerdictRed, result.Verdict);
        Assert.Contains("SAFETY_FLOOR_WFE", result.Rejections);
    }

    [Fact]
    public void HardRejection_FewTrades_ForcesRed()
    {
        var stageResults = BuildAllStages(strong: true);
        stageResults[1] = BuildStageResult(1, "BasicProfitability", passed: true, new Dictionary<string, double>
        {
            ["netProfit"] = 5000,
            ["profitFactor"] = 2.0,
            ["tradeCount"] = 20, // below 30 safety floor
            ["maxDrawdownPct"] = 15,
            ["tStatistic"] = 2.5,
        });
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 5);

        Assert.Equal(CompositeScoreResult.VerdictRed, result.Verdict);
        Assert.Contains("SAFETY_FLOOR_TRADES", result.Rejections);
    }

    [Fact]
    public void HardRejection_CostStressUnprofitable_ForcesRed()
    {
        var stageResults = BuildAllStages(strong: true);
        stageResults[6] = BuildStageResult(6, "MonteCarloPnlDeltasPermutation", passed: true, new Dictionary<string, double>
        {
            ["bootstrapDd95"] = 20,
            ["observedDdPct"] = 15,
            ["ddMultiplier"] = 1.2,
            ["probabilityOfRuin"] = 0.02,
            ["permutationPValue"] = 0.01,
            ["observedSharpe"] = 1.5,
            ["costStressNetProfit"] = -500, // unprofitable under stress
            ["originalCommissions"] = 100,
            ["costStressMultiplier"] = 2.0,
        });
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 5);

        Assert.Equal(CompositeScoreResult.VerdictRed, result.Verdict);
        Assert.Contains("COST_STRESS_UNPROFITABLE", result.Rejections);
    }

    [Fact]
    public void NoSurvivors_ForcesRed()
    {
        var stageResults = BuildPartialStages(upToStage: 3);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 0);

        Assert.Equal(CompositeScoreResult.VerdictRed, result.Verdict);
        Assert.Contains("NO_SURVIVORS", result.Rejections);
        Assert.Contains("FAILS", result.VerdictSummary);
    }

    [Fact]
    public void PipelineStoppedEarly_OnlyScoresAvailableCategories()
    {
        // Only stages 0-3 present (pipeline stopped at stage 3)
        var stageResults = BuildPartialStages(upToStage: 3);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 2);

        // Should still produce a score using available categories (Data, Stats, Params)
        Assert.True(result.CompositeScore > 0, "Score should be > 0 with 3 available categories");
        Assert.True(result.CategoryScores.ContainsKey(CompositeScoreResult.CategoryData));
        Assert.True(result.CategoryScores.ContainsKey(CompositeScoreResult.CategoryStats));
        Assert.True(result.CategoryScores.ContainsKey(CompositeScoreResult.CategoryParams));
        // Missing categories should have 0 score
        Assert.Equal(0.0, result.CategoryScores[CompositeScoreResult.CategoryWfo]);
        Assert.Equal(0.0, result.CategoryScores[CompositeScoreResult.CategoryWfm]);
    }

    [Fact]
    public void EmptyStageResults_ReturnsRedZeroScore()
    {
        var stageResults = new List<StageResultRecord>();
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 0, candidatesOut: 0);

        Assert.Equal(CompositeScoreResult.VerdictRed, result.Verdict);
        Assert.Equal(0.0, result.CompositeScore);
        Assert.Contains("NO_SURVIVORS", result.Rejections);
    }

    [Fact]
    public void MissingStage_WeightRedistributed()
    {
        // Build all stages but remove stage 5 (WFM) — its 10% should be redistributed
        var stageResults = BuildAllStages(strong: true);
        stageResults.RemoveAt(5); // Remove WFM

        var profile = ValidationThresholdProfile.CryptoStandard();
        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 5);

        // Score should still be high since all present categories are strong
        Assert.True(result.CompositeScore >= 65.0, $"Expected >= 65 with redistributed weights, got {result.CompositeScore}");
        Assert.Equal(0.0, result.CategoryScores[CompositeScoreResult.CategoryWfm]);
    }

    [Fact]
    public void VerdictSummary_ContainsScoreAndSurvivorCount()
    {
        var stageResults = BuildAllStages(strong: true);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 5);

        Assert.Contains("/100", result.VerdictSummary);
        Assert.Contains("5/100", result.VerdictSummary);
    }

    [Fact]
    public void MultipleHardRejections_AllReported()
    {
        var stageResults = BuildAllStages(strong: true);
        // Low WFE + excessive PBO
        stageResults[4] = BuildStageResult(4, "WalkForwardOptimization", passed: true, new Dictionary<string, double>
        {
            ["wfe"] = 0.20,
            ["profitableWindowsPct"] = 0.80,
            ["oosDrawdownExcessPct"] = 0.10,
            ["windowCount"] = 6,
        });
        stageResults[7] = BuildStageResult(7, "SelectionBiasAudit", passed: true, new Dictionary<string, double>
        {
            ["pbo"] = 0.70,
            ["profitableSubPeriodsPct"] = 0.90,
            ["equityCurveR2"] = 0.95,
            ["sharpeCoV"] = 0.2,
            ["sharpeDecaySlope"] = 0.0,
            ["isDecaying"] = 0,
        });
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 100, candidatesOut: 5);

        Assert.Equal(CompositeScoreResult.VerdictRed, result.Verdict);
        Assert.Contains("SAFETY_FLOOR_WFE", result.Rejections);
        Assert.Contains("SAFETY_FLOOR_PBO", result.Rejections);
    }

    [Theory]
    [InlineData(0.0, 100.0)]    // stable slope → best score
    [InlineData(-0.005, 50.0)]  // mild decay → midpoint
    [InlineData(-0.01, 0.0)]    // serious decay → worst score
    [InlineData(0.001, 100.0)]  // slightly positive → clamped to best
    [InlineData(-0.02, 0.0)]    // extreme decay → clamped to worst
    public void SubPeriod_SharpeDecaySlope_HigherIsBetter(double slope, double expectedScore)
    {
        // Isolate sharpeDecaySlope as the only metric in stage 7
        var stageResults = new List<StageResultRecord>
        {
            BuildStageResult(7, "SelectionBiasAudit", passed: true, new Dictionary<string, double>
            {
                ["sharpeDecaySlope"] = slope,
            }),
        };
        var profile = ValidationThresholdProfile.CryptoStandard();

        var result = CompositeScoreCalculator.Calculate(stageResults, profile, candidatesIn: 10, candidatesOut: 1);

        Assert.Equal(expectedScore, result.CategoryScores[CompositeScoreResult.CategorySubPeriod], precision: 1);
    }

    // --- Test helpers ---

    private static List<StageResultRecord> BuildAllStages(bool strong, bool marginal = false)
    {
        var list = new List<StageResultRecord>(8);

        // Stage 0: PreFlight (pass-through, no scored metrics)
        list.Add(BuildStageResult(0, "PreFlight", passed: true, new Dictionary<string, double>()));

        if (strong)
        {
            list.Add(BuildStageResult(1, "BasicProfitability", passed: true, new Dictionary<string, double>
            {
                ["netProfit"] = 25000,
                ["profitFactor"] = 2.5,
                ["tradeCount"] = 250,
                ["maxDrawdownPct"] = 12,
                ["tStatistic"] = 3.5,
            }));
            list.Add(BuildStageResult(2, "StatisticalSignificance", passed: true, new Dictionary<string, double>
            {
                ["dsr"] = 0.001,
                ["psr"] = 0.99,
                ["sharpe"] = 2.0,
                ["profitFactor"] = 2.5,
                ["recoveryFactor"] = 4.0,
                ["skewness"] = 0.5,
                ["excessKurtosis"] = 1.0,
            }));
            list.Add(BuildStageResult(3, "ParameterLandscape", passed: true, new Dictionary<string, double>
            {
                ["meanFitnessRetention"] = 0.85,
                ["primaryClusterConcentration"] = 0.80,
                ["silhouetteScore"] = 0.65,
                ["clusterCount"] = 2,
            }));
            list.Add(BuildStageResult(4, "WalkForwardOptimization", passed: true, new Dictionary<string, double>
            {
                ["wfe"] = 0.75,
                ["profitableWindowsPct"] = 0.90,
                ["oosDrawdownExcessPct"] = 0.10,
                ["windowCount"] = 8,
            }));
            list.Add(BuildStageResult(5, "WalkForwardMatrix", passed: true, new Dictionary<string, double>
            {
                ["clusterPassCount"] = 15,
                ["totalCells"] = 18,
                ["optimalReoptPeriod"] = 8,
                ["clusterRows"] = 4,
                ["clusterCols"] = 3,
            }));
            list.Add(BuildStageResult(6, "MonteCarloPnlDeltasPermutation", passed: true, new Dictionary<string, double>
            {
                ["bootstrapDd95"] = 18,
                ["observedDdPct"] = 12,
                ["ddMultiplier"] = 1.1,
                ["probabilityOfRuin"] = 0.01,
                ["permutationPValue"] = 0.005,
                ["observedSharpe"] = 2.0,
                ["costStressNetProfit"] = 15000,
                ["originalCommissions"] = 500,
                ["costStressMultiplier"] = 2.0,
            }));
            list.Add(BuildStageResult(7, "SelectionBiasAudit", passed: true, new Dictionary<string, double>
            {
                ["pbo"] = 0.10,
                ["numCombinations"] = 12870,
                ["profitableSubPeriodsPct"] = 0.90,
                ["equityCurveR2"] = 0.95,
                ["sharpeCoV"] = 0.20,
                ["sharpeDecaySlope"] = 0.001,
                ["isDecaying"] = 0,
            }));
        }
        else if (marginal)
        {
            list.Add(BuildStageResult(1, "BasicProfitability", passed: true, new Dictionary<string, double>
            {
                ["netProfit"] = 5000,
                ["profitFactor"] = 1.3,
                ["tradeCount"] = 100,
                ["maxDrawdownPct"] = 25,
                ["tStatistic"] = 2.2,
            }));
            list.Add(BuildStageResult(2, "StatisticalSignificance", passed: true, new Dictionary<string, double>
            {
                ["dsr"] = 0.04,
                ["psr"] = 0.96,
                ["sharpe"] = 0.7,
                ["profitFactor"] = 1.3,
                ["recoveryFactor"] = 1.8,
                ["skewness"] = 0.1,
                ["excessKurtosis"] = 0.5,
            }));
            list.Add(BuildStageResult(3, "ParameterLandscape", passed: true, new Dictionary<string, double>
            {
                ["meanFitnessRetention"] = 0.55,
                ["primaryClusterConcentration"] = 0.55,
                ["silhouetteScore"] = 0.40,
                ["clusterCount"] = 3,
            }));
            list.Add(BuildStageResult(4, "WalkForwardOptimization", passed: true, new Dictionary<string, double>
            {
                ["wfe"] = 0.52,
                ["profitableWindowsPct"] = 0.72,
                ["oosDrawdownExcessPct"] = 0.35,
                ["windowCount"] = 6,
            }));
            list.Add(BuildStageResult(5, "WalkForwardMatrix", passed: true, new Dictionary<string, double>
            {
                ["clusterPassCount"] = 9,
                ["totalCells"] = 18,
                ["optimalReoptPeriod"] = 8,
                ["clusterRows"] = 3,
                ["clusterCols"] = 3,
            }));
            list.Add(BuildStageResult(6, "MonteCarloPnlDeltasPermutation", passed: true, new Dictionary<string, double>
            {
                ["bootstrapDd95"] = 28,
                ["observedDdPct"] = 20,
                ["ddMultiplier"] = 1.4,
                ["probabilityOfRuin"] = 0.08,
                ["permutationPValue"] = 0.04,
                ["observedSharpe"] = 0.7,
                ["costStressNetProfit"] = 2000,
                ["originalCommissions"] = 500,
                ["costStressMultiplier"] = 2.0,
            }));
            list.Add(BuildStageResult(7, "SelectionBiasAudit", passed: true, new Dictionary<string, double>
            {
                ["pbo"] = 0.35,
                ["numCombinations"] = 12870,
                ["profitableSubPeriodsPct"] = 0.72,
                ["equityCurveR2"] = 0.86,
                ["sharpeCoV"] = 0.40,
                ["sharpeDecaySlope"] = -0.0005,
                ["isDecaying"] = 0,
            }));
        }
        else
        {
            // Weak metrics
            list.Add(BuildStageResult(1, "BasicProfitability", passed: true, new Dictionary<string, double>
            {
                ["netProfit"] = 500,
                ["profitFactor"] = 1.05,
                ["tradeCount"] = 35,
                ["maxDrawdownPct"] = 35,
                ["tStatistic"] = 2.1,
            }));
            list.Add(BuildStageResult(2, "StatisticalSignificance", passed: true, new Dictionary<string, double>
            {
                ["dsr"] = 0.04,
                ["psr"] = 0.55,
                ["sharpe"] = 0.2,
                ["profitFactor"] = 1.05,
                ["recoveryFactor"] = 0.6,
                ["skewness"] = -0.5,
                ["excessKurtosis"] = 3.0,
            }));
            list.Add(BuildStageResult(3, "ParameterLandscape", passed: true, new Dictionary<string, double>
            {
                ["meanFitnessRetention"] = 0.35,
                ["primaryClusterConcentration"] = 0.32,
                ["silhouetteScore"] = 0.20,
                ["clusterCount"] = 5,
            }));
            list.Add(BuildStageResult(4, "WalkForwardOptimization", passed: true, new Dictionary<string, double>
            {
                ["wfe"] = 0.35,
                ["profitableWindowsPct"] = 0.40,
                ["oosDrawdownExcessPct"] = 0.45,
                ["windowCount"] = 5,
            }));
            list.Add(BuildStageResult(5, "WalkForwardMatrix", passed: true, new Dictionary<string, double>
            {
                ["clusterPassCount"] = 3,
                ["totalCells"] = 18,
                ["optimalReoptPeriod"] = 6,
                ["clusterRows"] = 1,
                ["clusterCols"] = 3,
            }));
            list.Add(BuildStageResult(6, "MonteCarloPnlDeltasPermutation", passed: true, new Dictionary<string, double>
            {
                ["bootstrapDd95"] = 40,
                ["observedDdPct"] = 30,
                ["ddMultiplier"] = 2.5,
                ["probabilityOfRuin"] = 0.30,
                ["permutationPValue"] = 0.08,
                ["observedSharpe"] = 0.2,
                ["costStressNetProfit"] = 100,
                ["originalCommissions"] = 500,
                ["costStressMultiplier"] = 2.0,
            }));
            list.Add(BuildStageResult(7, "SelectionBiasAudit", passed: true, new Dictionary<string, double>
            {
                ["pbo"] = 0.55,
                ["numCombinations"] = 12870,
                ["profitableSubPeriodsPct"] = 0.40,
                ["equityCurveR2"] = 0.55,
                ["sharpeCoV"] = 0.80,
                ["sharpeDecaySlope"] = -0.008,
                ["isDecaying"] = 1,
            }));
        }

        return list;
    }

    private static List<StageResultRecord> BuildPartialStages(int upToStage)
    {
        var all = BuildAllStages(strong: true);
        return all.Take(upToStage + 1).ToList();
    }

    private static StageResultRecord BuildStageResult(
        int stageNumber, string stageName, bool passed, Dictionary<string, double> metrics)
    {
        var verdict = new CandidateVerdict(TrialId, passed, passed ? null : "FAILED", metrics);
        return new StageResultRecord
        {
            ValidationRunId = RunId,
            StageNumber = stageNumber,
            StageName = stageName,
            CandidatesIn = 1,
            CandidatesOut = passed ? 1 : 0,
            DurationMs = 10,
            CandidateVerdictsJson = JsonSerializer.Serialize(new[] { verdict }, JsonOptions),
        };
    }
}
