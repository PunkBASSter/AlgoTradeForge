using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Tests.Validation.TestHelpers;
using Xunit;
using AlgoTradeForge.Domain.Validation;
using AlgoTradeForge.Domain.Validation.Stages;

namespace AlgoTradeForge.Domain.Tests.Validation.Stages;

public class PreFlightStageTests
{
    private readonly PreFlightStage _stage = new();

    [Fact]
    public void StageMetadata_IsCorrect()
    {
        Assert.Equal(0, _stage.StageNumber);
        Assert.Equal("PreFlight", _stage.StageName);
    }

    [Fact]
    public void AllChecksPass_SurvivesAll()
    {
        var context = CreateContext(trialCount: 3, barCount: 500, totalCombinations: 100);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.SurvivingIndices.Count);
        Assert.Equal(3, result.Verdicts.Count);
        Assert.All(result.Verdicts, v =>
        {
            Assert.True(v.Passed);
            Assert.Null(v.ReasonCode);
            Assert.True(v.Metrics.ContainsKey("minBarCount"));
            Assert.True(v.Metrics.ContainsKey("minBtlBars"));
        });
    }

    [Fact]
    public void EmptyCandidates_ReturnsEmpty()
    {
        var context = CreateContext(trialCount: 0, barCount: 500, totalCombinations: 100);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.Empty(result.Verdicts);
    }

    // ---- MinBTL tests ----

    [Fact]
    public void InsufficientBars_RejectsAllWithInsufficientDataLength()
    {
        // With 100K combinations, MinBTL ≈ ceil(1.0 * sqrt(2*ln(100000)) * 10) ≈ 48
        // 10 bars is far too few
        var context = CreateContext(trialCount: 3, barCount: 10, totalCombinations: 100_000);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.All(result.Verdicts, v =>
        {
            Assert.False(v.Passed);
            Assert.Equal("INSUFFICIENT_DATA_LENGTH", v.ReasonCode);
        });
    }

    [Fact]
    public void CheckMinBtl_SmallN_RequiresAtLeast30Bars()
    {
        // With N=2, the formula gives very small MinBTL, but floor is 30
        var result = PreFlightStage.CheckMinBtl(barCount: 25, totalCombinations: 2, safetyFactor: 1.0);
        Assert.False(result.Passed);
        Assert.Equal(30, result.MinBtlBars);

        var result2 = PreFlightStage.CheckMinBtl(barCount: 30, totalCombinations: 2, safetyFactor: 1.0);
        Assert.True(result2.Passed);
    }

    [Fact]
    public void CheckMinBtl_LargeN_ScalesSublinearly()
    {
        // N=1000 → sqrt(2*ln(1000)) ≈ sqrt(13.8) ≈ 3.72, *10 ≈ 38
        var r1000 = PreFlightStage.CheckMinBtl(barCount: 1000, totalCombinations: 1000, safetyFactor: 1.0);
        Assert.True(r1000.Passed);
        Assert.True(r1000.MinBtlBars < 100);

        // N=100000 → sqrt(2*ln(100000)) ≈ sqrt(23.0) ≈ 4.80, *10 ≈ 48
        var r100k = PreFlightStage.CheckMinBtl(barCount: 1000, totalCombinations: 100_000, safetyFactor: 1.0);
        Assert.True(r100k.Passed);
        Assert.True(r100k.MinBtlBars < 100);
    }

    [Fact]
    public void CheckMinBtl_SafetyFactor_IncreasesRequirement()
    {
        var base_ = PreFlightStage.CheckMinBtl(barCount: 1000, totalCombinations: 1000, safetyFactor: 1.0);
        var strict = PreFlightStage.CheckMinBtl(barCount: 1000, totalCombinations: 1000, safetyFactor: 2.0);

        Assert.True(strict.MinBtlBars > base_.MinBtlBars);
    }

    [Fact]
    public void CheckMinBtl_ZeroCombinations_AlwaysPasses()
    {
        var result = PreFlightStage.CheckMinBtl(barCount: 1, totalCombinations: 0, safetyFactor: 1.0);
        Assert.True(result.Passed);
    }

    // ---- Timestamp gap tests ----

    [Fact]
    public void NoGaps_PassesDataQuality()
    {
        // Evenly spaced timestamps
        var timestamps = Enumerable.Range(0, 100).Select(i => (long)i * 60_000).ToArray();
        var result = PreFlightStage.CheckTimestampGaps(timestamps, maxGapRatio: 3.0, maxAllowedGaps: 10);

        Assert.True(result.Passed);
        Assert.Equal(0, result.GapCount);
    }

    [Fact]
    public void ManyGaps_FailsDataQuality()
    {
        // Create timestamps with 15 gaps (exceeding the default threshold of 10)
        var timestamps = new long[100];
        for (var i = 0; i < 100; i++)
        {
            timestamps[i] = i < 50
                ? i * 60_000L
                : 50 * 60_000L + (i - 50) * (i % 3 == 0 ? 300_000L : 60_000L); // every 3rd has 5x gap
        }

        var result = PreFlightStage.CheckTimestampGaps(timestamps, maxGapRatio: 3.0, maxAllowedGaps: 5);

        Assert.False(result.Passed);
        Assert.True(result.GapCount > 5);
    }

    [Fact]
    public void ExcessiveGaps_RejectsAllCandidates()
    {
        // Create 50 timestamps: mostly 60s apart, but with 5 big gaps (10x normal) interspersed.
        // Median interval = 60_000; gaps of 600_000 are 10x > 3x threshold.
        var timestamps = new long[50];
        timestamps[0] = 0;
        for (var i = 1; i < 50; i++)
        {
            var isGap = i == 10 || i == 20 || i == 30 || i == 35 || i == 45;
            timestamps[i] = timestamps[i - 1] + (isGap ? 600_000L : 60_000L);
        }

        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            PreFlight = new ValidationThresholdProfile.Stage0PreFlightThresholds
            {
                MaxGapRatio = 3.0,
                MaxAllowedGaps = 2, // Only allow 2 gaps, but we have 5
            },
        };

        var context = CreateContextWithTimestamps(timestamps, trialCount: 2, totalCombinations: 5, profile: profile);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.All(result.Verdicts, v =>
        {
            Assert.False(v.Passed);
            Assert.Equal("EXCESSIVE_DATA_GAPS", v.ReasonCode);
        });
    }

    [Fact]
    public void CheckTimestampGaps_SingleBar_Passes()
    {
        var result = PreFlightStage.CheckTimestampGaps([100L], maxGapRatio: 3.0, maxAllowedGaps: 10);
        Assert.True(result.Passed);
    }

    // ---- Cost model tests ----

    [Fact]
    public void ZeroCommissions_RejectsAllWhenRequired()
    {
        var context = CreateContext(trialCount: 3, barCount: 500, totalCombinations: 10,
            commissions: 0m);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Empty(result.SurvivingIndices);
        Assert.All(result.Verdicts, v =>
        {
            Assert.False(v.Passed);
            Assert.Equal("ZERO_COST_MODEL", v.ReasonCode);
        });
    }

    [Fact]
    public void ZeroCommissions_PassesWhenNotRequired()
    {
        var profile = ValidationThresholdProfile.CryptoStandard() with
        {
            PreFlight = new ValidationThresholdProfile.Stage0PreFlightThresholds
            {
                RequireNonZeroCosts = false,
            },
        };

        var context = CreateContext(trialCount: 3, barCount: 500, totalCombinations: 10,
            commissions: 0m, profile: profile);

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.SurvivingIndices.Count);
    }

    // ---- NaN P&L tests ----

    [Fact]
    public void NaNInPnl_RejectsAffectedCandidate()
    {
        var ts = Enumerable.Range(0, 100).Select(i => (long)i * 60_000).ToArray();
        var matrix = new double[3][];
        for (var i = 0; i < 3; i++)
        {
            matrix[i] = Enumerable.Repeat(1.0, 100).ToArray();
        }

        matrix[1][50] = double.NaN; // NaN in trial 1

        var cache = SimulationCacheTestHelper.Create(ts, matrix);
        var trials = CreateTrials(3);

        var context = new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = [0, 1, 2],
            TotalCombinations = 10,
        };

        var result = _stage.Execute(context, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.SurvivingIndices.Count);
        Assert.Contains(0, result.SurvivingIndices);
        Assert.DoesNotContain(1, result.SurvivingIndices);
        Assert.Contains(2, result.SurvivingIndices);

        var nanVerdict = result.Verdicts.Single(v => !v.Passed);
        Assert.Equal("PNL_CONTAINS_NAN", nanVerdict.ReasonCode);
    }

    // ---- Helpers ----

    private static ValidationContext CreateContext(
        int trialCount,
        int barCount,
        long totalCombinations,
        decimal commissions = 5m,
        ValidationThresholdProfile? profile = null)
    {
        var ts = Enumerable.Range(0, barCount).Select(i => (long)i * 60_000).ToArray();
        return CreateContextWithTimestamps(ts, trialCount, totalCombinations, commissions, profile);
    }

    private static ValidationContext CreateContextWithTimestamps(
        long[] timestamps,
        int trialCount,
        long totalCombinations,
        decimal commissions = 5m,
        ValidationThresholdProfile? profile = null)
    {
        var barCount = timestamps.Length;
        var matrix = new double[trialCount][];
        for (var i = 0; i < trialCount; i++)
        {
            matrix[i] = Enumerable.Repeat(1.0, barCount).ToArray();
        }

        var cache = SimulationCacheTestHelper.Create(timestamps, matrix);
        var trials = CreateTrials(trialCount, commissions);

        return new ValidationContext
        {
            Cache = cache,
            Trials = trials,
            Profile = profile ?? ValidationThresholdProfile.CryptoStandard(),
            ActiveCandidateIndices = Enumerable.Range(0, trialCount).ToList(),
            TotalCombinations = totalCombinations,
        };
    }

    private static IReadOnlyList<TrialSummary> CreateTrials(int count, decimal commissions = 5m) =>
        Enumerable.Range(0, count)
            .Select(i => new TrialSummary
            {
                Index = i,
                Id = Guid.NewGuid(),
                Metrics = new PerformanceMetrics
                {
                    TotalTrades = 50,
                    WinningTrades = 30,
                    LosingTrades = 20,
                    NetProfit = 100m,
                    GrossProfit = 200m,
                    GrossLoss = -100m,
                    TotalCommissions = commissions,
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
            }).ToList();
}
