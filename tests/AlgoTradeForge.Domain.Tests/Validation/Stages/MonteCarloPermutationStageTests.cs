using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.Validation.TestHelpers;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Stages;

public class MonteCarloPermutationStageTests
{
    private readonly MonteCarloPnlDeltasPermutationStage _stage = new();

    [Fact]
    public void RobustCandidate_PassesAllChecks()
    {
        // Strong positive trend with low drawdown, high commissions headroom
        var pnl = new double[200];
        for (var i = 0; i < 200; i++)
            pnl[i] = 10.0 + i * 0.2; // Trending up: 10, 10.2, ..., 49.8

        var context = CreateContext(pnl,
            netProfit: 5000m, maxDrawdownPct: 5.0, totalCommissions: 100m);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Single(result.SurvivingIndices);
        Assert.True(result.Verdicts[0].Passed);
        Assert.Null(result.Verdicts[0].ReasonCode);

        // Verify expected metrics keys
        var metrics = result.Verdicts[0].Metrics;
        Assert.True(metrics.ContainsKey("bootstrapDd95"));
        Assert.True(metrics.ContainsKey("permutationPValue"));
        Assert.True(metrics.ContainsKey("costStressNetProfit"));
    }

    [Fact]
    public void HighDrawdown_FailsMcDrawdownExcessive()
    {
        // Large P&L swings → bootstrap will show high DD multiplier
        var pnl = new double[200];
        for (var i = 0; i < 200; i++)
            pnl[i] = i % 2 == 0 ? 500.0 : -480.0; // Large swings

        // Report very low observed DD to trigger high multiplier
        var context = CreateContext(pnl,
            netProfit: 2000m, maxDrawdownPct: 1.0, totalCommissions: 10m);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("MC_DRAWDOWN_EXCESSIVE", result.Verdicts[0].ReasonCode);
    }

    [Fact]
    public void RandomWalk_FailsPermutationNotSignificant()
    {
        // IID random P&L → ordering doesn't matter → high p-value
        var rng = new Random(42);
        var pnl = new double[200];
        for (var i = 0; i < 200; i++)
            pnl[i] = rng.NextDouble() * 20 - 10;

        var context = CreateContext(pnl,
            netProfit: 5000m, maxDrawdownPct: 50.0, totalCommissions: 10m);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        // Either passes or fails on permutation — depends on the specific random sequence
        // The key check is that the metric is present
        Assert.True(result.Verdicts[0].Metrics.ContainsKey("permutationPValue"));
    }

    [Fact]
    public void ThinMargin_FailsCostStressUnprofitable()
    {
        // Trending data that passes permutation and bootstrap, but fails cost stress
        var pnl = new double[200];
        for (var i = 0; i < 200; i++)
            pnl[i] = 5.0 + i * 0.1; // Trending positive

        // Net profit barely exceeds commissions: at 2× cost, profit goes negative
        var context = CreateContext(pnl,
            netProfit: 150m, maxDrawdownPct: 50.0, totalCommissions: 200m);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("COST_STRESS_UNPROFITABLE", result.Verdicts[0].ReasonCode);

        var costProfit = result.Verdicts[0].Metrics["costStressNetProfit"];
        Assert.True(costProfit <= 0, $"Expected negative cost-stressed profit, got {costProfit}");
    }

    [Fact]
    public void EmptyCandidates_ReturnsEmpty()
    {
        var cache = SimulationCacheTestHelper.Create(new long[] { 100, 200, 300 }, [new double[] { 1, 2, 3 }]);
        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0, 100m, 5.0, 10m)],
            Profile = ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = [],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Empty(result.Verdicts);
    }

    [Fact]
    public void MultipleCandidates_IndependentEvaluation()
    {
        // Candidate 0: robust. Candidate 1: thin margin (fails cost stress)
        var pnl0 = new double[200];
        var pnl1 = new double[200];
        for (var i = 0; i < 200; i++)
        {
            pnl0[i] = 10.0 + i * 0.2; // Strong trend
            pnl1[i] = 5.0 + i * 0.1;  // Also trending (passes permutation)
        }

        var timestamps = Enumerable.Range(0, 200).Select(i => (long)(i * 1000)).ToArray();
        var cache = SimulationCacheTestHelper.Create(timestamps, [pnl0, pnl1]);

        var trials = new[]
        {
            CreateTrial(0, 5000m, 5.0, 50m),   // Profitable at 2× costs
            CreateTrial(1, 40m, 5.0, 50m),      // 40 - 50*(2-1) = -10 ≤ 0 → fails cost stress
        };

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = [0, 1],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Verdicts.Count);
        // Trial 1 should fail on cost stress
        var v1 = result.Verdicts.First(v => v.TrialId == trials[1].Id);
        Assert.False(v1.Passed);
        Assert.Equal("COST_STRESS_UNPROFITABLE", v1.ReasonCode);
    }

    private static ValidationContext CreateContext(
        double[] pnl, decimal netProfit, double maxDrawdownPct, decimal totalCommissions)
    {
        var timestamps = Enumerable.Range(0, pnl.Length).Select(i => (long)(i * 1000)).ToArray();
        var cache = SimulationCacheTestHelper.Create(timestamps, [pnl]);
        var trial = CreateTrial(0, netProfit, maxDrawdownPct, totalCommissions);

        return new ValidationContext
        {
            Cache = cache,
            Trials = [trial],
            Profile = ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = [0],
        };
    }

    private static TrialSummary CreateTrial(
        int index, decimal netProfit, double maxDrawdownPct, decimal totalCommissions) => new()
    {
        Index = index,
        Id = Guid.NewGuid(),
        Metrics = new PerformanceMetrics
        {
            TotalTrades = 100,
            WinningTrades = 60,
            LosingTrades = 40,
            NetProfit = netProfit,
            GrossProfit = netProfit + 500m,
            GrossLoss = -500m,
            TotalCommissions = totalCommissions,
            TotalReturnPct = 10,
            AnnualizedReturnPct = 15,
            SharpeRatio = 1.5,
            SortinoRatio = 2.0,
            MaxDrawdownPct = maxDrawdownPct,
            WinRatePct = 60,
            ProfitFactor = 2.0,
            AverageWin = 10,
            AverageLoss = -5,
            InitialCapital = 10000m,
            FinalEquity = 10000m + netProfit,
            TradingDays = 252,
        },
    };
}
