using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Validation;

public class ValidationPipelineTests
{
    [Fact]
    public void AllCandidatesSurvive_WhenMetricsStrong()
    {
        var (cache, trials) = CreateTrials(3, strongMetrics: true);
        var profile = ValidationThresholdProfile.CryptoStandard();
        var pipeline = new ValidationPipeline();

        var (stageResults, survivors) = pipeline.Execute(
            cache, trials, profile, Guid.NewGuid(), null, CancellationToken.None);

        Assert.Equal(ValidationPipeline.StageCount, stageResults.Count);
        Assert.Equal(3, survivors.Count);
    }

    [Fact]
    public void EarlyExit_WhenAllEliminated()
    {
        var (cache, trials) = CreateTrials(3, strongMetrics: false);
        var profile = ValidationThresholdProfile.CryptoStandard();
        var pipeline = new ValidationPipeline();

        var (stageResults, survivors) = pipeline.Execute(
            cache, trials, profile, Guid.NewGuid(), null, CancellationToken.None);

        // BasicProfitability (stage 1) should eliminate all; pipeline breaks after stage 2
        // (PreFlight runs first as stage 0, then BasicProfitability as stage 1, then break)
        Assert.Equal(2, stageResults.Count);
        Assert.Equal("PreFlight", stageResults[0].StageName);
        Assert.Equal("BasicProfitability", stageResults[1].StageName);
        Assert.Empty(survivors);
    }

    [Fact]
    public void ProgressCallback_CalledPerStage()
    {
        var (cache, trials) = CreateTrials(2, strongMetrics: true);
        var profile = ValidationThresholdProfile.CryptoStandard();
        var pipeline = new ValidationPipeline();
        var progressCalls = new List<(int Current, int Total)>();

        pipeline.Execute(cache, trials, profile, Guid.NewGuid(),
            (current, total) => progressCalls.Add((current, total)), CancellationToken.None);

        // Should have StageCount + 1 calls: one per stage + final completion
        Assert.Equal(ValidationPipeline.StageCount + 1, progressCalls.Count);

        // Verify incrementing current values
        for (var i = 0; i < progressCalls.Count - 1; i++)
        {
            Assert.Equal(i, progressCalls[i].Current);
            Assert.Equal(ValidationPipeline.StageCount, progressCalls[i].Total);
        }

        // Final call: current == total
        var final = progressCalls[^1];
        Assert.Equal(ValidationPipeline.StageCount, final.Current);
        Assert.Equal(ValidationPipeline.StageCount, final.Total);
    }

    [Fact]
    public void CancellationToken_Respected()
    {
        var (cache, trials) = CreateTrials(2, strongMetrics: true);
        var profile = ValidationThresholdProfile.CryptoStandard();
        var pipeline = new ValidationPipeline();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            pipeline.Execute(cache, trials, profile, Guid.NewGuid(), null, cts.Token));
    }

    [Fact]
    public void StageResults_CandidateFlowIsConsistent()
    {
        // Mix: 2 strong trials + 1 weak (negative NetProfit)
        var strongTrials = CreateTrialSummaries(2, strongMetrics: true, startIndex: 0);
        var weakTrials = CreateTrialSummaries(1, strongMetrics: false, startIndex: 2);
        var allTrials = strongTrials.Concat(weakTrials).ToArray();

        var pnlRows = allTrials.Select((_, i) =>
            i < 2
                ? new double[] { 10.0, 20.0, 30.0 }
                : new double[] { -50.0, -60.0, -70.0 }
        ).ToArray();

        var cache = new SimulationCache([100, 200, 300], pnlRows);
        var profile = ValidationThresholdProfile.CryptoStandard();
        var pipeline = new ValidationPipeline();

        var (stageResults, _) = pipeline.Execute(
            cache, allTrials, profile, Guid.NewGuid(), null, CancellationToken.None);

        // Verify chain: CandidatesIn[N+1] == CandidatesOut[N]
        for (var i = 1; i < stageResults.Count; i++)
            Assert.Equal(stageResults[i - 1].CandidatesOut, stageResults[i].CandidatesIn);
    }

    private static (SimulationCache Cache, TrialSummary[] Trials) CreateTrials(
        int count, bool strongMetrics)
    {
        var trials = CreateTrialSummaries(count, strongMetrics, 0);

        // Use 500 bars so that WFO (5 windows) and WFM (up to 15 periods) have enough data.
        // Consistent positive P&L ensures WFE ≈ 1.0 for strong metrics.
        const int barCount = 500;
        var pnlRows = trials.Select((_, t) =>
        {
            var row = new double[barCount];
            var perBar = strongMetrics ? 10.0 + t * 0.5 : -5.0;
            for (var b = 0; b < barCount; b++) row[b] = perBar;
            return row;
        }).ToArray();

        var timestamps = new long[barCount];
        for (var i = 0; i < barCount; i++) timestamps[i] = i * 86400000L;

        var cache = new SimulationCache(timestamps, pnlRows);
        return (cache, trials);
    }

    private static TrialSummary[] CreateTrialSummaries(int count, bool strongMetrics, int startIndex)
    {
        return Enumerable.Range(startIndex, count).Select(i => new TrialSummary
        {
            Index = i,
            Id = Guid.NewGuid(),
            Metrics = strongMetrics ? StrongMetrics() : WeakMetrics(),
        }).ToArray();
    }

    private static PerformanceMetrics StrongMetrics() => new()
    {
        TotalTrades = 100,
        WinningTrades = 60,
        LosingTrades = 40,
        NetProfit = 5000m,
        GrossProfit = 10000m,
        GrossLoss = -5000m,
        TotalCommissions = 50m,
        TotalReturnPct = 50,
        AnnualizedReturnPct = 25,
        SharpeRatio = 2.5,
        SortinoRatio = 3.0,
        MaxDrawdownPct = 10,
        WinRatePct = 60,
        ProfitFactor = 2.0,
        AverageWin = 166.67,
        AverageLoss = -125,
        InitialCapital = 10000m,
        FinalEquity = 15000m,
        TradingDays = 252,
    };

    private static PerformanceMetrics WeakMetrics() => new()
    {
        TotalTrades = 10,
        WinningTrades = 3,
        LosingTrades = 7,
        NetProfit = -500m,
        GrossProfit = 200m,
        GrossLoss = -700m,
        TotalCommissions = 20m,
        TotalReturnPct = -5,
        AnnualizedReturnPct = -5,
        SharpeRatio = -0.5,
        SortinoRatio = -0.3,
        MaxDrawdownPct = 30,
        WinRatePct = 30,
        ProfitFactor = 0.3,
        AverageWin = 66.67,
        AverageLoss = -100,
        InitialCapital = 10000m,
        FinalEquity = 9500m,
        TradingDays = 252,
    };
}
