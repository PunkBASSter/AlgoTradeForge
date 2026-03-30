using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Validation;
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

    private static ValidationContext CreateContext(params TrialSummary[] trials)
    {
        var barCount = 10;
        var cache = new SimulationCache(
            Enumerable.Range(0, barCount).Select(i => (long)(i * 86400000)).ToArray(),
            trials.Length > 0
                ? trials.Select(_ => Enumerable.Range(0, barCount).Select(i => 1.0).ToArray()).ToArray()
                : []);

        return new ValidationContext
        {
            Cache = cache,
            Trials = trials.ToList(),
            Profile = ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = Enumerable.Range(0, trials.Length).ToList(),
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
