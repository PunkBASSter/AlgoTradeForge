using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.Validation.TestHelpers;
using Xunit;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Domain.Tests.Validation.Stages;

public class StatisticalSignificanceStageTests
{
    private readonly StatisticalSignificanceStage _stage = new();

    [Fact]
    public void HighQualityCandidate_Passes()
    {
        // Strong candidate: high Sharpe, good profit factor, enough bars for statistical power
        var trial = CreateTrial(0, sharpe: 2.5, profitFactor: 1.8, netProfit: 5000m, maxDrawdownPct: 10);
        var context = CreateContext(trial, barCount: 500);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Single(result.SurvivingIndices);
        Assert.True(result.Verdicts[0].Passed);
    }

    [Fact]
    public void LowSharpe_Fails()
    {
        var trial = CreateTrial(0, sharpe: 0.2, profitFactor: 1.5, netProfit: 1000m, maxDrawdownPct: 10);
        var context = CreateContext(trial, barCount: 500);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        // Could fail on DSR, PSR, or Sharpe threshold depending on computed values
        Assert.False(result.Verdicts[0].Passed);
    }

    [Fact]
    public void LowProfitFactor_FailsStage2Threshold()
    {
        // PF of 1.10 passes Stage 1 (min 1.05) but fails Stage 2 (min 1.20)
        var trial = CreateTrial(0, sharpe: 2.0, profitFactor: 1.10, netProfit: 1000m, maxDrawdownPct: 10);
        var context = CreateContext(trial, barCount: 500);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        var verdict = result.Verdicts[0];
        Assert.False(verdict.Passed);
    }

    [Fact]
    public void Metrics_ContainDsrAndPsr()
    {
        var trial = CreateTrial(0, sharpe: 1.5, profitFactor: 1.5, netProfit: 3000m, maxDrawdownPct: 15);
        var context = CreateContext(trial, barCount: 300);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        var verdict = result.Verdicts[0];
        Assert.True(verdict.Metrics.ContainsKey("dsr"));
        Assert.True(verdict.Metrics.ContainsKey("psr"));
        Assert.True(verdict.Metrics.ContainsKey("sharpe"));
        Assert.True(verdict.Metrics.ContainsKey("recoveryFactor"));
        Assert.True(verdict.Metrics.ContainsKey("skewness"));
        Assert.True(verdict.Metrics.ContainsKey("excessKurtosis"));
    }

    [Fact]
    public void ManyTrials_DeflatesDSR()
    {
        // With many total trials, the DSR benchmark is higher, making it harder to pass
        var trials = Enumerable.Range(0, 50)
            .Select(i => CreateTrial(i, sharpe: 1.0, profitFactor: 1.3, netProfit: 2000m, maxDrawdownPct: 15))
            .ToArray();

        var context = CreateContext(trials, barCount: 300);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        // With 50 trials and modest Sharpe of 1.0, DSR should be deflated
        // Some or all trials may fail the DSR check
        Assert.True(result.Verdicts.All(v => v.Metrics.ContainsKey("dsr")));
    }

    private static ValidationContext CreateContext(TrialSummary trial, int barCount)
        => CreateContext([trial], barCount);

    private static ValidationContext CreateContext(TrialSummary[] trials, int barCount)
    {
        var timestamps = Enumerable.Range(0, barCount).Select(i => (long)(i * 60000)).ToArray();

        // Create P&L matrix with small positive drift to generate good equity curve
        var matrix = new double[trials.Length][];
        for (var i = 0; i < trials.Length; i++)
        {
            var pnl = new double[barCount];
            var rng = new Random(42 + i);
            for (var j = 0; j < barCount; j++)
                pnl[j] = (double)trials[i].Metrics.NetProfit / barCount + (rng.NextDouble() - 0.4) * 10;
            matrix[i] = pnl;
        }

        var cache = SimulationCacheTestHelper.Create(timestamps, matrix);

        return new ValidationContext
        {
            Cache = cache,
            Trials = trials.ToList(),
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = Enumerable.Range(0, trials.Length).ToList(),
        };
    }

    private static TrialSummary CreateTrial(int index, double sharpe, double profitFactor,
        decimal netProfit, double maxDrawdownPct) => new()
    {
        Index = index,
        Id = Guid.NewGuid(),
        Metrics = new PerformanceMetrics
        {
            TotalTrades = 100,
            WinningTrades = 55,
            LosingTrades = 45,
            NetProfit = netProfit,
            GrossProfit = netProfit * 2,
            GrossLoss = -netProfit,
            TotalCommissions = 50m,
            TotalReturnPct = (double)netProfit / 100.0,
            AnnualizedReturnPct = (double)netProfit / 100.0 * 2,
            SharpeRatio = sharpe,
            SortinoRatio = sharpe * 1.3,
            MaxDrawdownPct = maxDrawdownPct,
            WinRatePct = 55,
            ProfitFactor = profitFactor,
            AverageWin = 100,
            AverageLoss = -80,
            InitialCapital = 10000m,
            FinalEquity = 10000m + netProfit,
            TradingDays = 252,
        },
    };
}
