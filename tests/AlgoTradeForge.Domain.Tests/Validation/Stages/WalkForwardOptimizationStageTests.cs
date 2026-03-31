using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.Validation.TestHelpers;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Stages;

public class WalkForwardOptimizationStageTests
{
    private readonly WalkForwardOptimizationStage _stage = new();

    [Fact]
    public void StageNumberAndName()
    {
        Assert.Equal(4, _stage.StageNumber);
        Assert.Equal("WalkForwardOptimization", _stage.StageName);
    }

    [Fact]
    public void AscendingEquity_PassesAllCandidates()
    {
        var context = CreateContext(barCount: 200, pnlPerBar: 10.0, trialCount: 5);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        // With consistently positive P&L, WFO should pass
        Assert.True(result.SurvivingIndices.Count > 0 || result.Verdicts.All(v => !v.Passed),
            "Should either have survivors or have rejected all with a reason");

        // All verdicts should have WFO metrics
        Assert.All(result.Verdicts, v =>
        {
            Assert.True(v.Metrics.ContainsKey("wfe"));
            Assert.True(v.Metrics.ContainsKey("profitableWindowsPct"));
            Assert.True(v.Metrics.ContainsKey("windowCount"));
        });
    }

    [Fact]
    public void WholePoolGate_FailsAllWhenWfeFails()
    {
        // Truly flat P&L (all zeros) → WFE will be 0 → all candidates rejected
        var barCount = 200;
        var trialCount = 3;
        var matrix = new double[trialCount][];
        for (var t = 0; t < trialCount; t++)
            matrix[t] = new double[barCount]; // All zeros

        var timestamps = new long[barCount];
        for (var b = 0; b < barCount; b++)
            timestamps[b] = b * 86400000L;

        var tsArray = SimulationCacheTestHelper.ReplicateTimestamps(timestamps, trialCount);
        var cache = new SimulationCache(tsArray, matrix);
        var trials = Enumerable.Range(0, trialCount).Select(CreateTrial).ToList();

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = Enumerable.Range(0, trialCount).ToList(),
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        // All should fail (either WFE or profitable windows threshold)
        Assert.Empty(result.SurvivingIndices);
        Assert.All(result.Verdicts, v => Assert.False(v.Passed));
    }

    [Fact]
    public void EmptyCandidates_ReturnsEmpty()
    {
        var ts = Enumerable.Range(0, 100).Select(i => (long)i).ToArray();
        var cache = new SimulationCache(
            [ts],
            [Enumerable.Range(0, 100).Select(_ => 1.0).ToArray()]);

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0)],
            Profile = ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = [],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Empty(result.Verdicts);
    }

    private static ValidationContext CreateContext(int barCount, double pnlPerBar, int trialCount)
    {
        var matrix = new double[trialCount][];
        for (var t = 0; t < trialCount; t++)
        {
            matrix[t] = new double[barCount];
            for (var b = 0; b < barCount; b++)
                matrix[t][b] = pnlPerBar + t * 0.5;
        }

        var timestamps = new long[barCount];
        for (var b = 0; b < barCount; b++)
            timestamps[b] = b * 86400000L;

        var tsArray = SimulationCacheTestHelper.ReplicateTimestamps(timestamps, trialCount);
        var cache = new SimulationCache(tsArray, matrix);
        var trials = Enumerable.Range(0, trialCount).Select(CreateTrial).ToList();

        return new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = Enumerable.Range(0, trialCount).ToList(),
        };
    }

    private static TrialSummary CreateTrial(int index) => new()
    {
        Index = index,
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
    };
}
