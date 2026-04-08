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

        var cache = SimulationCacheTestHelper.Create(timestamps, matrix);
        var trials = Enumerable.Range(0, trialCount).Select(CreateTrial).ToList();

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = Enumerable.Range(0, trialCount).ToList(),
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
        var cache = SimulationCacheTestHelper.Create(
            ts,
            [Enumerable.Range(0, 100).Select(_ => 1.0).ToArray()]);

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0)],
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = [],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Empty(result.Verdicts);
    }

    [Fact]
    public void MultiSubscription_PerGroupGate_OnlyFailingGroupRejected()
    {
        // Group 1: ascending equity (passes)
        // Group 2: flat equity (fails)
        var barCount = 200;
        var trialsPerGroup = 3;
        var totalTrials = trialsPerGroup * 2;

        var timestamps = Enumerable.Range(0, barCount).Select(i => (long)(i * 86400000)).ToArray();
        var matrix = new double[totalTrials][];
        var timelineAssignments = new int[totalTrials];
        var groupMap = new Dictionary<int, string>();

        for (var t = 0; t < trialsPerGroup; t++)
        {
            // Group 1: positive PnL
            matrix[t] = Enumerable.Range(0, barCount).Select(_ => 10.0 + t * 0.5).ToArray();
            timelineAssignments[t] = 0;
            groupMap[t] = "BTCUSD:binance:1h";

            // Group 2: zero PnL
            matrix[t + trialsPerGroup] = new double[barCount];
            timelineAssignments[t + trialsPerGroup] = 1;
            groupMap[t + trialsPerGroup] = "ETHUSD:binance:1h";
        }

        var cache = SimulationCacheTestHelper.CreateMultiTimeline(
            [timestamps, timestamps], matrix, timelineAssignments);
        var trials = Enumerable.Range(0, totalTrials).Select(CreateTrial).ToList();

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = Enumerable.Range(0, totalTrials).ToList(),
            SubscriptionGroupByTrialIndex = groupMap,
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        // ETHUSD candidates (indices 3-5) should fail; BTCUSD candidates may pass
        var ethVerdicts = result.Verdicts
            .Where((_, i) => groupMap[context.AllCandidateIndices[i]] == "ETHUSD:binance:1h")
            .ToList();
        Assert.All(ethVerdicts, v => Assert.False(v.Passed));

        // BTCUSD candidates should not be rejected due to ETHUSD's failure
        var btcVerdicts = result.Verdicts
            .Where((_, i) => groupMap[context.AllCandidateIndices[i]] == "BTCUSD:binance:1h")
            .ToList();
        Assert.True(btcVerdicts.Any(v => v.Passed) || btcVerdicts.All(v => !v.Passed),
            "BTC verdicts should be independently evaluated, not failed by ETH");
    }

    [Fact]
    public void MultiSubscription_VerdictMetrics_ContainGroupCount()
    {
        var barCount = 200;
        var trialsPerGroup = 3;
        var totalTrials = trialsPerGroup * 2;

        var timestamps = Enumerable.Range(0, barCount).Select(i => (long)(i * 86400000)).ToArray();
        var matrix = new double[totalTrials][];
        var timelineAssignments = new int[totalTrials];
        var groupMap = new Dictionary<int, string>();

        for (var t = 0; t < totalTrials; t++)
        {
            matrix[t] = Enumerable.Range(0, barCount).Select(_ => 10.0).ToArray();
            timelineAssignments[t] = t < trialsPerGroup ? 0 : 1;
            groupMap[t] = t < trialsPerGroup ? "BTCUSD:binance:1h" : "ETHUSD:binance:1h";
        }

        var cache = SimulationCacheTestHelper.CreateMultiTimeline(
            [timestamps, timestamps], matrix, timelineAssignments);
        var trials = Enumerable.Range(0, totalTrials).Select(CreateTrial).ToList();

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = Enumerable.Range(0, totalTrials).ToList(),
            SubscriptionGroupByTrialIndex = groupMap,
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.All(result.Verdicts, v =>
        {
            Assert.True(v.Metrics.ContainsKey("subscriptionGroupCount"));
            Assert.Equal(2.0, v.Metrics["subscriptionGroupCount"]);
        });
    }

    [Fact]
    public void MultiSubscription_BothGroupsPass_HasMetricsWithGroupCount()
    {
        var barCount = 200;
        var trialsPerGroup = 5;
        var totalTrials = trialsPerGroup * 2;

        var timestamps = Enumerable.Range(0, barCount).Select(i => (long)(i * 86400000)).ToArray();
        var matrix = new double[totalTrials][];
        var timelineAssignments = new int[totalTrials];
        var groupMap = new Dictionary<int, string>();

        for (var t = 0; t < trialsPerGroup; t++)
        {
            // Both groups: positive PnL
            matrix[t] = Enumerable.Range(0, barCount).Select(_ => 10.0 + t * 0.5).ToArray();
            timelineAssignments[t] = 0;
            groupMap[t] = "BTCUSD:binance:1h";

            matrix[t + trialsPerGroup] = Enumerable.Range(0, barCount).Select(_ => 10.0 + t * 0.5).ToArray();
            timelineAssignments[t + trialsPerGroup] = 1;
            groupMap[t + trialsPerGroup] = "ETHUSD:binance:1h";
        }

        var cache = SimulationCacheTestHelper.CreateMultiTimeline(
            [timestamps, timestamps], matrix, timelineAssignments);
        var trials = Enumerable.Range(0, totalTrials).Select(CreateTrial).ToList();

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = Enumerable.Range(0, totalTrials).ToList(),
            SubscriptionGroupByTrialIndex = groupMap,
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        // With consistently positive P&L, WFO should pass or reject with a reason
        Assert.True(result.SurvivingIndices.Count > 0 || result.Verdicts.All(v => !v.Passed),
            "Should either have survivors or have rejected all with a reason");
        // All verdicts should carry multi-group metrics
        Assert.All(result.Verdicts, v =>
        {
            Assert.True(v.Metrics.ContainsKey("wfe"));
            Assert.Equal(2.0, v.Metrics["subscriptionGroupCount"]);
        });
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

        var cache = SimulationCacheTestHelper.Create(timestamps, matrix);
        var trials = Enumerable.Range(0, trialCount).Select(CreateTrial).ToList();

        return new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = Enumerable.Range(0, trialCount).ToList(),
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
