using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.Validation.TestHelpers;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Validation.Stages;

public class SelectionBiasAuditStageTests
{
    private readonly SelectionBiasAuditStage _stage = new();

    [Fact]
    public void HighPbo_GateFailsAll()
    {
        // Two identical trials → PBO ≈ 0.5, which exceeds default MaxPbo of 0.30
        var timestamps = Enumerable.Range(0, 200).Select(i => (long)(i * 1000)).ToArray();
        var rng = new Random(42);
        var pnl = new double[200];
        for (var i = 0; i < 200; i++) pnl[i] = rng.NextDouble() * 20 - 10;

        var cache = SimulationCacheTestHelper.Create(timestamps, [pnl, (double[])pnl.Clone()]);
        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0), CreateTrial(1)],
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = [0, 1],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.All(result.Verdicts, v =>
        {
            Assert.False(v.Passed);
            Assert.Equal("PBO_EXCESSIVE", v.ReasonCode);
            Assert.True(v.Metrics.ContainsKey("pbo"));
        });
    }

    [Fact]
    public void LowPbo_ConsistentCandidate_Passes()
    {
        // Two very different trials → low PBO
        // Trial 0: strong positive, Trial 1: strong negative
        var timestamps = Enumerable.Range(0, 200).Select(i => (long)(i * 1000)).ToArray();
        var pnl0 = new double[200];
        var pnl1 = new double[200];
        for (var i = 0; i < 200; i++)
        {
            pnl0[i] = 10.0 + i * 0.05; // Strong positive trend
            pnl1[i] = -5.0;             // Consistently negative
        }

        var cache = SimulationCacheTestHelper.Create(timestamps, [pnl0, pnl1]);

        // Use a permissive profile to focus on PBO gate
        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            SelectionBiasAudit = new ValidationThresholdProfile.Stage7SelectionBiasAuditThresholds
            {
                MaxPbo = 0.60, // Very permissive PBO
                MinProfitableSubPeriods = 0.50,
                MinR2 = 0.50,
                SubPeriodCount = 4,
                RollingSharpeWindow = 30,
                MaxSharpeDecaySlope = -1.0, // Very permissive decay threshold
                RegimeVolWindow = 30,
            },
        };

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0), CreateTrial(1)],
            Profile = profile,
            AllCandidateIndices = [0],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Single(result.SurvivingIndices);
        Assert.True(result.Verdicts[0].Passed);

        // Verify all metric keys present
        var m = result.Verdicts[0].Metrics;
        Assert.True(m.ContainsKey("pbo"));
        Assert.True(m.ContainsKey("profitableSubPeriodsPct"));
        Assert.True(m.ContainsKey("equityCurveR2"));
        Assert.True(m.ContainsKey("regimeCount"));
        Assert.True(m.ContainsKey("sharpeDecaySlope"));
    }

    [Fact]
    public void InconsistentSubPeriods_Fails()
    {
        // Trial 0: positive first half, negative second half → low profitable %
        var timestamps = Enumerable.Range(0, 200).Select(i => (long)(i * 1000)).ToArray();
        var pnl0 = new double[200];
        var pnl1 = new double[200];
        for (var i = 0; i < 100; i++) pnl0[i] = 20.0;
        for (var i = 100; i < 200; i++) pnl0[i] = -15.0;
        for (var i = 0; i < 200; i++) pnl1[i] = -1.0; // Bad trial (ensures low PBO for trial 0)

        var cache = SimulationCacheTestHelper.Create(timestamps, [pnl0, pnl1]);

        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            SelectionBiasAudit = new ValidationThresholdProfile.Stage7SelectionBiasAuditThresholds
            {
                MaxPbo = 1.0, // Allow PBO gate to pass so subperiod check can fire
                MinProfitableSubPeriods = 0.90, // Strict: require 90% profitable sub-periods
                MinR2 = 0.10,
                SubPeriodCount = 4,
                RollingSharpeWindow = 30,
                MaxSharpeDecaySlope = -10.0,
                RegimeVolWindow = 30,
            },
        };

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0), CreateTrial(1)],
            Profile = profile,
            AllCandidateIndices = [0],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("SUBPERIOD_INCONSISTENT", result.Verdicts[0].ReasonCode);
    }

    [Fact]
    public void PoorR2_FailsEquityCurveIrregular()
    {
        // Erratic equity curve: random swings → low R²
        var timestamps = Enumerable.Range(0, 200).Select(i => (long)(i * 1000)).ToArray();
        var rng = new Random(42);
        var pnl0 = new double[200];
        var pnl1 = new double[200];
        // Generate an equity curve that is clearly non-linear (quadratic or noisy)
        for (var i = 0; i < 200; i++)
        {
            // Big random swings: makes equity curve very noisy
            pnl0[i] = rng.NextDouble() * 400 - 190; // Range [-190, 210], slight positive bias
            pnl1[i] = -1.0;
        }

        var cache = SimulationCacheTestHelper.Create(timestamps, [pnl0, pnl1]);

        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            SelectionBiasAudit = new ValidationThresholdProfile.Stage7SelectionBiasAuditThresholds
            {
                MaxPbo = 0.60,
                MinProfitableSubPeriods = 0.10,
                MinR2 = 0.95, // Very strict R² requirement
                SubPeriodCount = 4,
                RollingSharpeWindow = 30,
                MaxSharpeDecaySlope = -10.0,
                RegimeVolWindow = 30,
            },
        };

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0), CreateTrial(1)],
            Profile = profile,
            AllCandidateIndices = [0],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("EQUITY_CURVE_IRREGULAR", result.Verdicts[0].ReasonCode);
    }

    [Fact]
    public void DecayingAlpha_FailsAlphaDecayDetected()
    {
        // Strong early returns declining over time → negative Sharpe slope
        var timestamps = Enumerable.Range(0, 300).Select(i => (long)(i * 1000)).ToArray();
        var pnl0 = new double[300];
        var pnl1 = new double[300];
        for (var i = 0; i < 300; i++)
        {
            pnl0[i] = 30.0 - i * 0.15; // Starts at 30, ends at -15
            pnl1[i] = -1.0;
        }

        var cache = SimulationCacheTestHelper.Create(timestamps, [pnl0, pnl1]);

        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            SelectionBiasAudit = new ValidationThresholdProfile.Stage7SelectionBiasAuditThresholds
            {
                MaxPbo = 0.60,
                MinProfitableSubPeriods = 0.10,
                MinR2 = 0.10,
                SubPeriodCount = 4,
                RollingSharpeWindow = 30,
                MaxSharpeDecaySlope = -0.0001, // Strict: even slight decay fails
                RegimeVolWindow = 30,
            },
        };

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0), CreateTrial(1)],
            Profile = profile,
            AllCandidateIndices = [0],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Equal("ALPHA_DECAY_DETECTED", result.Verdicts[0].ReasonCode);
        Assert.True(result.Verdicts[0].Metrics["sharpeDecaySlope"] < 0);
    }

    [Fact]
    public void RegimeMetrics_AttachedButNotGate()
    {
        // Verify regime metrics are present even when regime would look "bad"
        var timestamps = Enumerable.Range(0, 200).Select(i => (long)(i * 1000)).ToArray();
        var pnl0 = new double[200];
        var pnl1 = new double[200];
        for (var i = 0; i < 200; i++)
        {
            pnl0[i] = 10.0 + i * 0.05;
            pnl1[i] = -1.0;
        }

        var cache = SimulationCacheTestHelper.Create(timestamps, [pnl0, pnl1]);

        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            SelectionBiasAudit = new ValidationThresholdProfile.Stage7SelectionBiasAuditThresholds
            {
                MaxPbo = 0.60,
                MinProfitableSubPeriods = 0.10,
                MinR2 = 0.10,
                SubPeriodCount = 4,
                RollingSharpeWindow = 30,
                MaxSharpeDecaySlope = -10.0,
                RegimeVolWindow = 30,
            },
        };

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0), CreateTrial(1)],
            Profile = profile,
            AllCandidateIndices = [0],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        var m = result.Verdicts[0].Metrics;
        Assert.True(m.ContainsKey("regimeCount"));
        Assert.True(m.ContainsKey("profitableRegimeCount"));
        Assert.True(m.ContainsKey("sharpeRangeMin"));
        Assert.True(m.ContainsKey("sharpeRangeMax"));
    }

    [Fact]
    public void EmptyCandidates_ReturnsEmpty()
    {
        var timestamps = Enumerable.Range(0, 100).Select(i => (long)(i * 1000)).ToArray();
        var cache = SimulationCacheTestHelper.Create(timestamps,
            [Enumerable.Repeat(1.0, 100).ToArray(), Enumerable.Repeat(2.0, 100).ToArray()]);

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = [CreateTrial(0), CreateTrial(1)],
            Profile = ValidationThresholdProfile.CryptoStandard(),
            AllCandidateIndices = [],
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Empty(result.Verdicts);
    }

    private static TrialSummary CreateTrial(int index) => new()
    {
        Index = index,
        Id = Guid.NewGuid(),
        Metrics = new PerformanceMetrics
        {
            TotalTrades = 100,
            WinningTrades = 60,
            LosingTrades = 40,
            NetProfit = 5000m,
            GrossProfit = 8000m,
            GrossLoss = -3000m,
            TotalCommissions = 50m,
            TotalReturnPct = 50,
            AnnualizedReturnPct = 25,
            SharpeRatio = 1.5,
            SortinoRatio = 2.0,
            MaxDrawdownPct = 15,
            WinRatePct = 60,
            ProfitFactor = 2.67,
            AverageWin = 133.33,
            AverageLoss = -75,
            InitialCapital = 10000m,
            FinalEquity = 15000m,
            TradingDays = 252,
        },
    };
}
