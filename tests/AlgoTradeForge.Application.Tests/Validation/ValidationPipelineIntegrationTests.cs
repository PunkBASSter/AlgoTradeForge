using System.Diagnostics;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Validation;

/// <summary>
/// Integration tests for the full validation pipeline, running all 8 stages
/// end-to-end with synthetic trial data.
/// </summary>
public class ValidationPipelineIntegrationTests
{
    [Fact]
    public void HappyPath_AllStagesRun_CompositeScoreComputed()
    {
        // 50 profitable trials × 500 bars
        var (cache, trials) = CreateSyntheticData(
            trialCount: 50, barCount: 500, meanPnlPerBar: 2.0, stdDev: 10.0, seed: 42);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var pipeline = new ValidationPipeline();
        var (results, survivors) = pipeline.Execute(
            cache, trials, profile, Guid.NewGuid(),
            onProgress: null, ct: CancellationToken.None,
            totalCombinations: 100);

        // Should have at least a few stage results (pipeline stops when candidates exhausted)
        Assert.NotEmpty(results);
        Assert.True(results.Count >= 2, $"Expected at least 2 stages, got {results.Count}");

        // Stage 0 (PreFlight) should pass all
        Assert.Equal(50, results[0].CandidatesIn);
        Assert.Equal(50, results[0].CandidatesOut);

        // Pipeline should produce a composite score
        var scoreResult = CompositeScoreCalculator.Calculate(
            results, profile, candidatesIn: 50, candidatesOut: survivors.Count);

        Assert.InRange(scoreResult.CompositeScore, 0, 100);
        Assert.NotNull(scoreResult.Verdict);
        Assert.NotNull(scoreResult.VerdictSummary);
    }

    [Fact]
    public void AllNegativeTrials_Stage1EliminatesAll_RedVerdict()
    {
        // 50 unprofitable trials
        var (cache, trials) = CreateSyntheticData(
            trialCount: 50, barCount: 500, meanPnlPerBar: -5.0, stdDev: 3.0, seed: 99);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var pipeline = new ValidationPipeline();
        var (results, survivors) = pipeline.Execute(
            cache, trials, profile, Guid.NewGuid(),
            onProgress: null, ct: CancellationToken.None,
            totalCombinations: 100);

        // Stage 1 should eliminate all (negative profit, low profit factor)
        var stage1 = results.First(r => r.StageNumber == 1);
        Assert.Equal(0, stage1.CandidatesOut);

        // No survivors
        Assert.Empty(survivors);

        // Composite score should be Red with NO_SURVIVORS rejection
        var scoreResult = CompositeScoreCalculator.Calculate(
            results, profile, candidatesIn: 50, candidatesOut: 0);

        Assert.Equal("Red", scoreResult.Verdict);
        Assert.Contains("NO_SURVIVORS", scoreResult.Rejections);
    }

    [Fact]
    public void PreFlightRejection_InsufficientData_RejectsAll()
    {
        // 10 bars but 100K combinations — MinBTL will fail
        var (cache, trials) = CreateSyntheticData(
            trialCount: 5, barCount: 10, meanPnlPerBar: 5.0, stdDev: 2.0, seed: 1);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var pipeline = new ValidationPipeline();
        var (results, survivors) = pipeline.Execute(
            cache, trials, profile, Guid.NewGuid(),
            onProgress: null, ct: CancellationToken.None,
            totalCombinations: 100_000);

        // Stage 0 should reject all
        var stage0 = results.First(r => r.StageNumber == 0);
        Assert.Equal(0, stage0.CandidatesOut);
        Assert.Empty(survivors);
    }

    [Fact]
    public void ProgressCallback_ReportsAllStages()
    {
        var (cache, trials) = CreateSyntheticData(
            trialCount: 10, barCount: 200, meanPnlPerBar: 1.0, stdDev: 5.0, seed: 7);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var progressUpdates = new List<(int Current, int Total)>();
        var pipeline = new ValidationPipeline();
        pipeline.Execute(
            cache, trials, profile, Guid.NewGuid(),
            onProgress: (c, t) => progressUpdates.Add((c, t)),
            ct: CancellationToken.None,
            totalCombinations: 50);

        // Should get at least 1 progress update (might get fewer if early exit)
        Assert.NotEmpty(progressUpdates);
        // Last update should be (8, 8) or the stage where all were eliminated
        var last = progressUpdates[^1];
        Assert.Equal(last.Total, ValidationPipeline.StageCount);
    }

    [Fact]
    public void Cancellation_StopsPipeline()
    {
        var (cache, trials) = CreateSyntheticData(
            trialCount: 50, barCount: 500, meanPnlPerBar: 2.0, stdDev: 10.0, seed: 42);
        var profile = ValidationThresholdProfile.CryptoStandard();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        var pipeline = new ValidationPipeline();
        Assert.Throws<OperationCanceledException>(() =>
            pipeline.Execute(cache, trials, profile, Guid.NewGuid(),
                onProgress: null, ct: cts.Token, totalCombinations: 100));
    }

    [Fact]
    public void PerformanceBenchmark_200Trials10KBars_CompletesInTime()
    {
        // Regression guardrail: full pipeline should complete in < 60s
        var (cache, trials) = CreateSyntheticData(
            trialCount: 200, barCount: 10_000, meanPnlPerBar: 0.5, stdDev: 5.0, seed: 42);
        var profile = ValidationThresholdProfile.CryptoStandard();

        var sw = Stopwatch.StartNew();
        var pipeline = new ValidationPipeline();
        pipeline.Execute(
            cache, trials, profile, Guid.NewGuid(),
            onProgress: null, ct: CancellationToken.None,
            totalCombinations: 1000);
        sw.Stop();

        // Should complete in under 60 seconds (typically < 15s)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
            $"Pipeline took {sw.Elapsed.TotalSeconds:F1}s, expected < 60s");
    }

    // ---- Helpers ----

    private static (SimulationCache Cache, TrialSummary[] Trials) CreateSyntheticData(
        int trialCount, int barCount, double meanPnlPerBar, double stdDev, int seed)
    {
        var rng = new Random(seed);
        var timestamps = new long[barCount];
        for (var i = 0; i < barCount; i++)
            timestamps[i] = 1704067200000L + i * 3_600_000L; // hourly bars

        var matrix = new double[trialCount][];
        var trials = new TrialSummary[trialCount];

        for (var t = 0; t < trialCount; t++)
        {
            var row = new double[barCount];
            double totalPnl = 0;
            double peak = 0;
            double maxDd = 0;

            for (var b = 0; b < barCount; b++)
            {
                // Box-Muller transform for normal distribution
                var u1 = rng.NextDouble();
                var u2 = rng.NextDouble();
                var z = Math.Sqrt(-2.0 * Math.Log(Math.Max(u1, 1e-10))) * Math.Cos(2.0 * Math.PI * u2);
                row[b] = meanPnlPerBar + stdDev * z;
                totalPnl += row[b];

                if (totalPnl > peak) peak = totalPnl;
                var dd = (peak - totalPnl) / Math.Max(10000 + peak, 1) * 100;
                if (dd > maxDd) maxDd = dd;
            }

            matrix[t] = row;

            var profitFactor = totalPnl > 0 ? 1.0 + totalPnl / (Math.Abs(totalPnl) + 1000) : 0.5;
            var sharpe = barCount > 1
                ? meanPnlPerBar / (stdDev / Math.Sqrt(252.0))
                : 0;

            trials[t] = new TrialSummary
            {
                Index = t,
                Id = Guid.NewGuid(),
                Metrics = new PerformanceMetrics
                {
                    TotalTrades = Math.Max(barCount / 10, 50),
                    WinningTrades = Math.Max(barCount / 20, 30),
                    LosingTrades = Math.Max(barCount / 20, 20),
                    NetProfit = (decimal)totalPnl,
                    GrossProfit = (decimal)Math.Max(totalPnl * 1.5, 100),
                    GrossLoss = (decimal)(-Math.Max(totalPnl * 0.5, 100)),
                    TotalCommissions = 50m,
                    TotalReturnPct = totalPnl / 100.0,
                    AnnualizedReturnPct = totalPnl / 100.0 * (252.0 / barCount),
                    SharpeRatio = sharpe,
                    SortinoRatio = sharpe * 1.2,
                    MaxDrawdownPct = Math.Max(maxDd, 1),
                    WinRatePct = 55,
                    ProfitFactor = profitFactor,
                    AverageWin = totalPnl > 0 ? totalPnl / Math.Max(barCount / 20, 1) : 1,
                    AverageLoss = -Math.Abs(totalPnl) / Math.Max(barCount / 20, 1) - 1,
                    InitialCapital = 10000m,
                    FinalEquity = 10000m + (decimal)totalPnl,
                    TradingDays = barCount,
                },
            };
        }

        var cache = new SimulationCache(timestamps, matrix);
        return (cache, trials);
    }
}
