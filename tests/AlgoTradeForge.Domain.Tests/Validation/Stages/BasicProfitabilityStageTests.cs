using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.Validation.TestHelpers;
using Xunit;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Domain.Tests.Validation.Stages;

public class BasicProfitabilityStageTests
{
    private readonly BasicProfitabilityStage _stage = new();

    [Fact]
    public void AllPass_WhenAllCandidatesMeetThresholds()
    {
        var context = CreateContext(
            CreateTrial(0, netProfit: 100m, profitFactor: 1.5, tradeCount: 50, maxDrawdownPct: 10));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Single(result.SurvivingIndices);
        Assert.Single(result.Verdicts);
        Assert.True(result.Verdicts[0].Passed);
    }

    [Fact]
    public void Fails_NegativeNetProfit()
    {
        var context = CreateContext(
            CreateTrial(0, netProfit: -50m, profitFactor: 0.8, tradeCount: 50, maxDrawdownPct: 10));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("NET_PROFIT_NEGATIVE", result.Verdicts[0].ReasonCode);
    }

    [Fact]
    public void Fails_LowProfitFactor()
    {
        var context = CreateContext(
            CreateTrial(0, netProfit: 100m, profitFactor: 1.01, tradeCount: 50, maxDrawdownPct: 10));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("PROFIT_FACTOR_BELOW_THRESHOLD", result.Verdicts[0].ReasonCode);
    }

    [Fact]
    public void Fails_InsufficientTrades()
    {
        var context = CreateContext(
            CreateTrial(0, netProfit: 100m, profitFactor: 1.5, tradeCount: 10, maxDrawdownPct: 10));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("INSUFFICIENT_TRADES", result.Verdicts[0].ReasonCode);
    }

    [Fact]
    public void Fails_ExcessiveDrawdown()
    {
        var context = CreateContext(
            CreateTrial(0, netProfit: 100m, profitFactor: 1.5, tradeCount: 50, maxDrawdownPct: 55));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("EXCESSIVE_DRAWDOWN", result.Verdicts[0].ReasonCode);
    }

    [Fact]
    public void Mixed_SomeSurviveSomeFail()
    {
        var context = CreateContext(
            CreateTrial(0, netProfit: 200m, profitFactor: 2.0, tradeCount: 60, maxDrawdownPct: 15),
            CreateTrial(1, netProfit: -10m, profitFactor: 0.9, tradeCount: 40, maxDrawdownPct: 30),
            CreateTrial(2, netProfit: 50m, profitFactor: 1.3, tradeCount: 35, maxDrawdownPct: 20));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.SurvivingIndices.Count);
        Assert.Contains(0, result.SurvivingIndices);
        Assert.Contains(2, result.SurvivingIndices);
        Assert.DoesNotContain(1, result.SurvivingIndices);
    }

    [Fact]
    public void ThresholdBoundary_ExactlyMinProfitFactor_Passes()
    {
        // MinProfitFactor default = 1.05. Exactly at threshold should pass (>=)
        var context = CreateContext(
            CreateTrial(0, netProfit: 100m, profitFactor: 1.05, tradeCount: 50, maxDrawdownPct: 10));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Single(result.SurvivingIndices);
    }

    [Fact]
    public void Fails_TStatisticBelowThreshold()
    {
        // Near-zero mean with high variance → low t-stat
        var noisyPnl = new double[] { 500, -490, 480, -470, 460, -450, 440, -430, 420, -410 };
        var context = CreateContext(
            [noisyPnl],
            CreateTrial(0, netProfit: 100m, profitFactor: 1.5, tradeCount: 50, maxDrawdownPct: 10));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("T_STATISTIC_BELOW_THRESHOLD", result.Verdicts[0].ReasonCode);
    }

    [Fact]
    public void TStatistic_MetricIsAttached()
    {
        var context = CreateContext(
            CreateTrial(0, netProfit: 100m, profitFactor: 1.5, tradeCount: 50, maxDrawdownPct: 10));

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.True(result.Verdicts[0].Metrics.ContainsKey("tStatistic"));
    }

    // Default P&L: steady positive returns giving t-stat > 2.0 on 10000 initial equity.
    // Each bar adds ~100, giving ~1% return with low variance.
    private static readonly double[] DefaultPnl =
        [100, 105, 98, 102, 101, 103, 99, 104, 97, 106, 100, 102, 98, 101, 103, 99, 105, 97, 104, 100];

    private static ValidationContext CreateContext(params TrialSummary[] trials) =>
        CreateContext(null, trials);

    private static ValidationContext CreateContext(double[][]? pnlMatrix, params TrialSummary[] trials)
    {
        var matrix = pnlMatrix ?? trials.Select(_ => (double[])DefaultPnl.Clone()).ToArray();
        var barCount = matrix[0].Length;
        var timestamps = Enumerable.Range(0, barCount).Select(i => (long)(i * 100)).ToArray();

        var cache = SimulationCacheTestHelper.Create(timestamps, matrix);

        return new ValidationContext
        {
            Cache = cache,
            Trials = trials.ToList(),
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = Enumerable.Range(0, trials.Length).ToList(),
        };
    }

    private static TrialSummary CreateTrial(int index, decimal netProfit, double profitFactor,
        int tradeCount, double maxDrawdownPct) => new()
    {
        Index = index,
        Id = Guid.NewGuid(),
        Metrics = new PerformanceMetrics
        {
            TotalTrades = tradeCount,
            WinningTrades = tradeCount / 2,
            LosingTrades = tradeCount / 2,
            NetProfit = netProfit,
            GrossProfit = netProfit > 0 ? netProfit * 2 : 10m,
            GrossLoss = netProfit > 0 ? -netProfit : -(10m - netProfit),
            TotalCommissions = 1m,
            TotalReturnPct = (double)netProfit / 100.0,
            AnnualizedReturnPct = (double)netProfit / 100.0,
            SharpeRatio = 1.5,
            SortinoRatio = 2.0,
            MaxDrawdownPct = maxDrawdownPct,
            WinRatePct = 50,
            ProfitFactor = profitFactor,
            AverageWin = 10,
            AverageLoss = -5,
            InitialCapital = 10000m,
            FinalEquity = 10000m + netProfit,
            TradingDays = 252,
        },
    };
}
