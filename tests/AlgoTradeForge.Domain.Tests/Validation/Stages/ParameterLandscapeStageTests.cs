using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.Validation.TestHelpers;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Results;
using AlgoTradeForge.Domain.Validation.Stages;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Stages;

public class ParameterLandscapeStageTests
{
    private readonly ParameterLandscapeStage _stage = new();

    [Fact]
    public void StageNumberAndName()
    {
        Assert.Equal(3, _stage.StageNumber);
        Assert.Equal("ParameterLandscape", _stage.StageName);
    }

    [Fact]
    public void NoParameters_PassesThroughWithReasonCode()
    {
        var context = CreateContext(
            CreateTrial(0, parameters: null),
            CreateTrial(1, parameters: null));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.SurvivingIndices.Count);
        Assert.All(result.Verdicts, v =>
        {
            Assert.True(v.Passed);
            Assert.Equal("NO_PARAMETERS", v.ReasonCode);
        });
    }

    [Fact]
    public void HighClusterConcentration_Passes()
    {
        // All trials have similar parameters → high concentration
        var trials = new List<TrialSummary>();
        for (var i = 0; i < 10; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["period"] = 20.0 + i * 0.1 },
                sharpe: 1.5, pf: 2.0));
        }

        var context = CreateContext(trials.ToArray());
        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(10, result.SurvivingIndices.Count);
        Assert.All(result.Verdicts, v => Assert.True(v.Passed));
    }

    [Fact]
    public void EmptyCandidates_ReturnsEmpty()
    {
        var context = CreateContext();

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Empty(result.Verdicts);
    }

    [Fact]
    public void VerdictMetrics_ContainExpectedKeys()
    {
        var trials = new List<TrialSummary>();
        for (var i = 0; i < 10; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["param1"] = (double)(i * 10) },
                sharpe: 1.5, pf: 2.0));
        }

        var context = CreateContext(trials.ToArray());
        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.All(result.Verdicts, v =>
        {
            Assert.True(v.Metrics.ContainsKey("primaryClusterConcentration"));
            Assert.True(v.Metrics.ContainsKey("silhouetteScore"));
            Assert.True(v.Metrics.ContainsKey("clusterCount"));
            Assert.True(v.Metrics.ContainsKey("meanFitnessRetention"));
        });
    }

    [Fact]
    public void SingleSubscription_NoGroupMap_IdenticalToBaseline()
    {
        // When SubscriptionGroupByTrialIndex is null, behavior should be
        // identical to pre-subscription-awareness (no crossSubscriptionStability key)
        var trials = new List<TrialSummary>();
        for (var i = 0; i < 10; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["period"] = 20.0 + i * 0.1 },
                sharpe: 1.5, pf: 2.0));
        }

        var context = CreateContext(trials.ToArray());
        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(10, result.SurvivingIndices.Count);
        // Single-group path does NOT emit crossSubscriptionStability
        Assert.All(result.Verdicts, v => Assert.False(v.Metrics.ContainsKey("crossSubscriptionStability")));
    }

    [Fact]
    public void MultiSubscription_CrossStabilityHigh_WhenCentroidsOverlap()
    {
        // Both groups share the SAME parameter range → centroids nearly identical → high stability
        var trials = new List<TrialSummary>();
        var groupMap = new Dictionary<int, string>();

        // Group 1 (BTCUSD): period = 20.0, 20.1, 20.2, 20.3, 20.4
        for (var i = 0; i < 5; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["period"] = 20.0 + i * 0.1 },
                sharpe: 1.5, pf: 2.0));
            groupMap[i] = "BTCUSD:binance:1h";
        }

        // Group 2 (ETHUSD): period = 20.0, 20.1, 20.2, 20.3, 20.4 (same range!)
        for (var i = 5; i < 10; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["period"] = 20.0 + (i - 5) * 0.1 },
                sharpe: 1.5, pf: 2.0));
            groupMap[i] = "ETHUSD:binance:1h";
        }

        var context = CreateContextWithGroupMap(trials.ToArray(), groupMap);
        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        // All should pass, crossSubscriptionStability should be 1.0 (identical centroids)
        Assert.True(result.SurvivingIndices.Count > 0);
        var firstVerdict = result.Verdicts.First(v => v.Passed);
        Assert.True(firstVerdict.Metrics.ContainsKey("crossSubscriptionStability"));
        Assert.True(firstVerdict.Metrics["crossSubscriptionStability"] >= 0.99,
            $"Expected stability >= 0.99, got {firstVerdict.Metrics["crossSubscriptionStability"]}");
    }

    [Fact]
    public void MultiSubscription_CrossStabilityLow_WhenCentroidsDiverge()
    {
        // Two groups with very different parameter values → low stability
        var trials = new List<TrialSummary>();
        var groupMap = new Dictionary<int, string>();

        // Group 1: period around 10
        for (var i = 0; i < 5; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["period"] = 10.0 + i * 0.1, ["threshold"] = 1.0 },
                sharpe: 1.5, pf: 2.0));
            groupMap[i] = "BTCUSD:binance:1h";
        }

        // Group 2: period around 100 (far away)
        for (var i = 5; i < 10; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["period"] = 100.0 + i * 0.1, ["threshold"] = 50.0 },
                sharpe: 1.5, pf: 2.0));
            groupMap[i] = "ETHUSD:binance:1h";
        }

        // Use a low threshold to trigger rejection
        var profile = new ValidationThresholdProfile
        {
            Name = "test",
            ParameterLandscape = new ValidationThresholdProfile.Stage3ParameterLandscapeThresholds
            {
                MinCrossSubscriptionStability = 0.95,
            },
        };

        var context = CreateContextWithGroupMap(trials.ToArray(), groupMap, profile);
        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        // Should have rejected due to cross-subscription stability
        var rejectedVerdicts = result.Verdicts.Where(v => !v.Passed).ToList();
        Assert.True(rejectedVerdicts.Count > 0);
        Assert.Contains(rejectedVerdicts, v => v.ReasonCode == "CROSS_SUBSCRIPTION_STABILITY_LOW");
    }

    [Fact]
    public void MultiSubscription_VerdictMetrics_ContainSubscriptionKeys()
    {
        var trials = new List<TrialSummary>();
        var groupMap = new Dictionary<int, string>();

        for (var i = 0; i < 10; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["period"] = 20.0 + i * 0.1 },
                sharpe: 1.5, pf: 2.0));
            groupMap[i] = i < 5 ? "BTCUSD:binance:1h" : "ETHUSD:binance:1h";
        }

        var context = CreateContextWithGroupMap(trials.ToArray(), groupMap);
        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.All(result.Verdicts, v =>
        {
            Assert.True(v.Metrics.ContainsKey("crossSubscriptionStability"));
            Assert.True(v.Metrics.ContainsKey("subscriptionGroupCount"));
            Assert.Equal(2.0, v.Metrics["subscriptionGroupCount"]);
        });
    }

    [Fact]
    public void ComputeCrossSubscriptionStability_ThreeGroups_ScoreBetweenZeroAndOne()
    {
        var perGroupResults = new Dictionary<string, ClusterAnalysisResult>
        {
            ["BTC"] = new()
            {
                PrimaryClusterConcentration = 0.8, ClusterCount = 1, SilhouetteScore = 0.7,
                ClusterCentroid = new Dictionary<string, double> { ["period"] = 20.0, ["threshold"] = 5.0 },
            },
            ["ETH"] = new()
            {
                PrimaryClusterConcentration = 0.75, ClusterCount = 1, SilhouetteScore = 0.65,
                ClusterCentroid = new Dictionary<string, double> { ["period"] = 25.0, ["threshold"] = 8.0 },
            },
            ["SOL"] = new()
            {
                PrimaryClusterConcentration = 0.7, ClusterCount = 1, SilhouetteScore = 0.6,
                ClusterCentroid = new Dictionary<string, double> { ["period"] = 30.0, ["threshold"] = 3.0 },
            },
        };

        var result = ParameterLandscapeStage.ComputeCrossSubscriptionStability(perGroupResults);

        Assert.Equal(3, result.GroupCount);
        Assert.InRange(result.StabilityScore, 0.0, 1.0);
        Assert.True(result.MeanCentroidDistance > 0);
    }

    [Fact]
    public void ComputeCrossSubscriptionStability_EmptyCentroids_ReturnsStabilityOne()
    {
        var perGroupResults = new Dictionary<string, ClusterAnalysisResult>
        {
            ["BTC"] = new()
            {
                PrimaryClusterConcentration = 0.8, ClusterCount = 1, SilhouetteScore = 0.7,
                ClusterCentroid = new Dictionary<string, double>(),
            },
            ["ETH"] = new()
            {
                PrimaryClusterConcentration = 0.75, ClusterCount = 1, SilhouetteScore = 0.65,
                ClusterCentroid = new Dictionary<string, double>(),
            },
        };

        var result = ParameterLandscapeStage.ComputeCrossSubscriptionStability(perGroupResults);

        Assert.Equal(1.0, result.StabilityScore);
        Assert.Equal(0, result.GroupCount);
    }

    private static ValidationContext CreateContext(params TrialSummary[] trials)
    {
        var barCount = 10;
        var timestamps = Enumerable.Range(0, barCount).Select(i => (long)(i * 86400000)).ToArray();
        var matrix = trials.Length > 0
            ? trials.Select(_ => Enumerable.Range(0, barCount).Select(i => 1.0).ToArray()).ToArray()
            : [];
        var cache = SimulationCacheTestHelper.Create(timestamps, matrix);

        return new ValidationContext
        {
            Cache = cache,
            Trials = trials.ToList(),
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = Enumerable.Range(0, trials.Length).ToList(),
        };
    }

    private static ValidationContext CreateContextWithGroupMap(
        TrialSummary[] trials,
        Dictionary<int, string> groupMap,
        ValidationThresholdProfile? profile = null)
    {
        var barCount = 10;
        var timestamps = Enumerable.Range(0, barCount).Select(i => (long)(i * 86400000)).ToArray();

        // Create separate timelines per group
        var distinctGroups = groupMap.Values.Distinct().ToList();
        var timelines = distinctGroups.Select(_ => timestamps).ToArray();
        var groupToTimeline = distinctGroups.Select((g, i) => (g, i)).ToDictionary(x => x.g, x => x.i);

        var matrix = trials.Select(_ => Enumerable.Range(0, barCount).Select(i => 1.0).ToArray()).ToArray();
        var timelineAssignments = Enumerable.Range(0, trials.Length)
            .Select(i => groupToTimeline[groupMap[i]])
            .ToArray();

        var cache = SimulationCacheTestHelper.CreateMultiTimeline(timelines, matrix, timelineAssignments);

        return new ValidationContext
        {
            Cache = cache,
            Trials = trials.ToList(),
            Profile = profile ?? ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = Enumerable.Range(0, trials.Length).ToList(),
            SubscriptionGroupByTrialIndex = groupMap,
        };
    }

    private static TrialSummary CreateTrial(int index,
        IReadOnlyDictionary<string, object>? parameters = null,
        double sharpe = 1.5, double pf = 2.0) => new()
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
            SharpeRatio = sharpe,
            SortinoRatio = 2.0,
            MaxDrawdownPct = 15,
            WinRatePct = 60,
            ProfitFactor = pf,
            AverageWin = 10,
            AverageLoss = -5,
            InitialCapital = 10000m,
            FinalEquity = 10100m,
            TradingDays = 252,
        },
        Parameters = parameters,
    };
}
