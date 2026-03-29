using AlgoTradeForge.Domain.Reporting;
using Xunit;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Domain.Tests.Validation.Stages;

public class StubStageTests
{
    [Theory]
    [InlineData(typeof(PreFlightStage), 0, "PreFlight")]
    [InlineData(typeof(MonteCarloPermutationStage), 6, "MonteCarloPermutation")]
    [InlineData(typeof(SelectionBiasAuditStage), 7, "SelectionBiasAudit")]
    public void StubStage_PassesAllCandidates(Type stageType, int expectedNumber, string expectedName)
    {
        var stage = (IValidationStage)Activator.CreateInstance(stageType)!;
        var context = CreateContext(3);

        Assert.Equal(expectedNumber, stage.StageNumber);
        Assert.Equal(expectedName, stage.StageName);

        var result = stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.SurvivingIndices.Count);
        Assert.Equal(3, result.Verdicts.Count);
        Assert.All(result.Verdicts, v =>
        {
            Assert.True(v.Passed);
            Assert.Equal("STUB", v.ReasonCode);
        });
    }

    [Theory]
    [InlineData(typeof(PreFlightStage))]
    [InlineData(typeof(MonteCarloPermutationStage))]
    [InlineData(typeof(SelectionBiasAuditStage))]
    public void StubStage_EmptyCandidates_ReturnsEmpty(Type stageType)
    {
        var stage = (IValidationStage)Activator.CreateInstance(stageType)!;
        var context = CreateContext(0);

        var result = stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Empty(result.Verdicts);
    }

    private static ValidationContext CreateContext(int trialCount)
    {
        var trials = Enumerable.Range(0, trialCount)
            .Select(i => new TrialSummary
            {
                Index = i,
                Id = Guid.NewGuid(),
                Metrics = new PerformanceMetrics
                {
                    TotalTrades = 50,
                    WinningTrades = 30,
                    LosingTrades = 20,
                    NetProfit = 100m,
                    GrossProfit = 200m,
                    GrossLoss = -100m,
                    TotalCommissions = 5m,
                    TotalReturnPct = 10,
                    AnnualizedReturnPct = 15,
                    SharpeRatio = 1.5,
                    SortinoRatio = 2.0,
                    MaxDrawdownPct = 15,
                    WinRatePct = 60,
                    ProfitFactor = 2.0,
                    AverageWin = 10,
                    AverageLoss = -5,
                    InitialCapital = 10000m,
                    FinalEquity = 10100m,
                    TradingDays = 252,
                },
            }).ToList();

        var cache = new SimulationCache(
            [100, 200, 300],
            trials.Select(_ => new double[] { 0.0, 0.0, 0.0 }).ToArray());

        return new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = Enumerable.Range(0, trialCount).ToList(),
        };
    }
}
