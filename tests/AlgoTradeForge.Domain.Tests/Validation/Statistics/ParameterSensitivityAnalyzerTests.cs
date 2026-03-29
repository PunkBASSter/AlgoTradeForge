using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Validation.Stages;
using AlgoTradeForge.Domain.Validation.Statistics;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Statistics;

public class ParameterSensitivityAnalyzerTests
{
    [Fact]
    public void StableNeighbors_HighRetention()
    {
        // All trials have similar fitness → retention near 1.0
        var trials = new List<TrialSummary>();
        for (var i = 0; i < 20; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["period"] = (double)(10 + i) },
                sharpe: 1.5, pf: 2.0, annRet: 15.0));
        }

        var result = ParameterSensitivityAnalyzer.Analyze(
            trials, [0], sensitivityRange: 0.50, maxDegradationPct: 0.30);

        Assert.True(result.MeanFitnessRetention > 0.7);
        Assert.True(result.PassedDegradationCheck);
    }

    [Fact]
    public void SensitiveParams_LowRetention()
    {
        // Candidate has high fitness, neighbors have much lower
        var trials = new List<TrialSummary>
        {
            CreateTrial(0,
                new Dictionary<string, object> { ["period"] = 50.0 },
                sharpe: 3.0, pf: 4.0, annRet: 50.0),
        };

        // Add neighbors with much worse fitness
        for (var i = 1; i < 20; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["period"] = 50.0 + i * 0.5 },
                sharpe: 0.2, pf: 1.1, annRet: 2.0));
        }

        var result = ParameterSensitivityAnalyzer.Analyze(
            trials, [0], sensitivityRange: 0.50, maxDegradationPct: 0.30);

        Assert.True(result.MeanFitnessRetention < 0.70);
        Assert.False(result.PassedDegradationCheck);
    }

    [Fact]
    public void SingleParamStrategy_Works()
    {
        var trials = new List<TrialSummary>();
        for (var i = 0; i < 10; i++)
        {
            trials.Add(CreateTrial(i,
                new Dictionary<string, object> { ["lookback"] = (double)(20 + i) },
                sharpe: 1.2, pf: 1.8, annRet: 12.0));
        }

        var result = ParameterSensitivityAnalyzer.Analyze(
            trials, [0, 1], sensitivityRange: 0.30, maxDegradationPct: 0.30);

        // Single param → no heatmaps (need ≥2 params for 2D heatmap)
        Assert.Empty(result.Heatmaps);
        Assert.True(result.PassedDegradationCheck);
    }

    [Fact]
    public void NonNumericParams_Skipped()
    {
        var trials = new List<TrialSummary>
        {
            CreateTrial(0,
                new Dictionary<string, object> { ["name"] = "test", ["value"] = 10.0 },
                sharpe: 1.5, pf: 2.0, annRet: 15.0),
            CreateTrial(1,
                new Dictionary<string, object> { ["name"] = "test2", ["value"] = 11.0 },
                sharpe: 1.4, pf: 1.9, annRet: 14.0),
        };

        var result = ParameterSensitivityAnalyzer.Analyze(
            trials, [0], sensitivityRange: 0.50, maxDegradationPct: 0.30);

        Assert.True(result.PassedDegradationCheck);
    }

    [Fact]
    public void NullParameters_ReturnsDefault()
    {
        var trials = new List<TrialSummary>
        {
            new()
            {
                Index = 0,
                Id = Guid.NewGuid(),
                Metrics = CreateMetrics(1.5, 2.0, 15.0),
                Parameters = null,
            },
        };

        var result = ParameterSensitivityAnalyzer.Analyze(
            trials, [0], sensitivityRange: 0.10, maxDegradationPct: 0.30);

        Assert.Equal(1.0, result.MeanFitnessRetention);
        Assert.True(result.PassedDegradationCheck);
    }

    private static TrialSummary CreateTrial(int index,
        Dictionary<string, object> parameters,
        double sharpe, double pf, double annRet) => new()
    {
        Index = index,
        Id = Guid.NewGuid(),
        Metrics = CreateMetrics(sharpe, pf, annRet),
        Parameters = parameters,
    };

    private static PerformanceMetrics CreateMetrics(double sharpe, double pf, double annRet) => new()
    {
        TotalTrades = 50,
        WinningTrades = 30,
        LosingTrades = 20,
        NetProfit = 1000m,
        GrossProfit = 2000m,
        GrossLoss = -1000m,
        TotalCommissions = 5m,
        TotalReturnPct = 10,
        AnnualizedReturnPct = annRet,
        SharpeRatio = sharpe,
        SortinoRatio = sharpe * 1.2,
        MaxDrawdownPct = 15,
        WinRatePct = 60,
        ProfitFactor = pf,
        AverageWin = 10,
        AverageLoss = -5,
        InitialCapital = 10000m,
        FinalEquity = 11000m,
        TradingDays = 252,
    };
}
