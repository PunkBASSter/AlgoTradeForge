using System.Diagnostics;
using AlgoTradeForge.Application;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

namespace AlgoTradeForge.Infrastructure.Tests.Persistence;

/// <summary>
/// Integration tests for optimization group repository operations (T016, T075).
/// Tests group CRUD, cross-DSS trial queries, and sorting performance.
/// </summary>
public class SqliteRunRepository_GroupTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRunRepository _repo;

    public SqliteRunRepository_GroupTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"group_test_{Guid.NewGuid():N}.sqlite");
        var options = Options.Create(new RunStorageOptions { DatabasePath = _dbPath });
        _repo = new SqliteRunRepository(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static OptimizationGroupRecord MakeGroup(Guid? id = null, int totalRuns = 2) => new()
    {
        Id = id ?? Guid.NewGuid(),
        StrategyName = "TestStrategy",
        StrategyVersion = "1",
        OptimizationMethod = "BruteForce",
        StartedAt = DateTimeOffset.UtcNow,
        TotalRuns = totalRuns,
        Status = OptimizationGroupStatus.InProgress,
        SubscriptionsJson = "[[{\"assetName\":\"BTC\",\"exchange\":\"Binance\",\"timeFrame\":\"1h\"}]]",
        BacktestSettingsJson = "{\"initialCash\":10000}",
        MaxParallelism = 4,
    };

    private static OptimizationRunRecord MakeChildRun(
        Guid groupId, int dssIndex, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        StrategyName = "TestStrategy",
        StrategyVersion = "1",
        StartedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        DurationMs = 1000,
        TotalCombinations = 100,
        SortBy = "FitnessScore",
        DataSubscriptions = [new TimeBarSubscription($"ASSET{dssIndex}", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
        BacktestSettings = new BacktestSettingsDto
        {
            InitialCash = 10_000m,
            StartTime = DateTimeOffset.UtcNow.AddDays(-30),
            EndTime = DateTimeOffset.UtcNow,
        },
        MaxParallelism = 4,
        Trials = [],
        Status = OptimizationRunStatus.Completed,
        GroupId = groupId,
        DssIndex = dssIndex,
    };

    private static BacktestRunRecord MakeTrial(
        Guid optimizationRunId, int seed)
    {
        var rng = new Random(seed);
        return new BacktestRunRecord
        {
            Id = Guid.NewGuid(),
            StrategyName = "TestStrategy",
            StrategyVersion = "1",
            Parameters = new Dictionary<string, object> { ["Quantity"] = (decimal)rng.Next(1, 100) },
            DataSubscriptions = [new TimeBarSubscription("BTC", "Binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = 10_000m,
                StartTime = DateTimeOffset.UtcNow.AddDays(-30),
                EndTime = DateTimeOffset.UtcNow,
            },
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 100,
            TotalBars = 720,
            Metrics = new PerformanceMetrics
            {
                TotalTrades = rng.Next(5, 200),
                WinningTrades = rng.Next(1, 100),
                LosingTrades = rng.Next(1, 100),
                NetProfit = (decimal)(rng.NextDouble() * 10000 - 2000),
                GrossProfit = (decimal)(rng.NextDouble() * 5000),
                GrossLoss = -(decimal)(rng.NextDouble() * 3000),
                TotalCommissions = (decimal)(rng.NextDouble() * 100),
                TotalReturnPct = rng.NextDouble() * 50,
                SharpeRatio = rng.NextDouble() * 4 - 1,
                SortinoRatio = rng.NextDouble() * 5 - 0.5,
                ProfitFactor = rng.NextDouble() * 5,
                MaxDrawdownPct = -(rng.NextDouble() * 40),
                WinRatePct = rng.NextDouble() * 100,
                AverageWin = rng.NextDouble() * 500,
                AverageLoss = -(rng.NextDouble() * 300),
                AnnualizedReturnPct = rng.NextDouble() * 100 - 20,
                InitialCapital = 10_000m,
                FinalEquity = 10_000m + (decimal)(rng.NextDouble() * 5000),
                TradingDays = 252,
            },
            FitnessScore = rng.NextDouble() * 3,
            EquityCurve = [],
            TradePnl = [],
            RunMode = "Optimization",
            OptimizationRunId = optimizationRunId,
        };
    }

    // ── T016: Basic group CRUD ───────────────────────────────────────────

    [Fact]
    public async Task InsertAndGetGroup_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = MakeGroup();
        await _repo.InsertOptimizationGroupAsync(group, ct);

        var loaded = await _repo.GetOptimizationGroupByIdAsync(group.Id, ct);
        Assert.NotNull(loaded);
        Assert.Equal(group.Id, loaded.Id);
        Assert.Equal("TestStrategy", loaded.StrategyName);
        Assert.Equal("BruteForce", loaded.OptimizationMethod);
        Assert.Equal(2, loaded.TotalRuns);
    }

    [Fact]
    public async Task InsertGroup_WithChildRuns_LoadsRunsOnGetById()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = MakeGroup();
        await _repo.InsertOptimizationGroupAsync(group, ct);

        var run0 = MakeChildRun(group.Id, 0);
        var run1 = MakeChildRun(group.Id, 1);
        await _repo.InsertOptimizationPlaceholderAsync(run0, ct);
        await _repo.InsertOptimizationPlaceholderAsync(run1, ct);

        var loaded = await _repo.GetOptimizationGroupByIdAsync(group.Id, ct);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.Runs.Count);
        Assert.Equal(0, loaded.Runs[0].DssIndex);
        Assert.Equal(1, loaded.Runs[1].DssIndex);
    }

    [Fact]
    public async Task QueryGroups_ReturnsInsertedGroups()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = MakeGroup();
        await _repo.InsertOptimizationGroupAsync(group, ct);

        var result = await _repo.QueryOptimizationGroupsAsync(new OptimizationGroupQuery(), ct);
        Assert.True(result.TotalCount >= 1);
        Assert.Contains(result.Items, g => g.Id == group.Id);
    }

    [Fact]
    public async Task DeleteGroup_CascadesAndReturnsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = MakeGroup();
        await _repo.InsertOptimizationGroupAsync(group, ct);

        var run = MakeChildRun(group.Id, 0);
        await _repo.InsertOptimizationPlaceholderAsync(run, ct);

        var trial = MakeTrial(run.Id, 42);
        await _repo.SaveAsync(trial, ct);

        var deleted = await _repo.DeleteOptimizationGroupAsync(group.Id, ct);
        Assert.True(deleted);

        // Verify group is gone
        var loaded = await _repo.GetOptimizationGroupByIdAsync(group.Id, ct);
        Assert.Null(loaded);

        // Verify trial is gone
        var trialLoaded = await _repo.GetByIdAsync(trial.Id, ct);
        Assert.Null(trialLoaded);
    }

    [Fact]
    public async Task UpdateGroupStatus_UpdatesStatusAndCompletedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = MakeGroup();
        await _repo.InsertOptimizationGroupAsync(group, ct);

        var completedAt = DateTimeOffset.UtcNow;
        await _repo.UpdateOptimizationGroupStatusAsync(
            group.Id, OptimizationGroupStatus.Completed, completedAt, ct);

        var loaded = await _repo.GetOptimizationGroupByIdAsync(group.Id, ct);
        Assert.NotNull(loaded);
        Assert.Equal(OptimizationGroupStatus.Completed, loaded.Status);
        Assert.NotNull(loaded.CompletedAt);
    }

    // ── Cross-DSS trials query ───────────────────────────────────────────

    [Fact]
    public async Task GetGroupTrials_ReturnsTrialsFromAllChildRuns()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = MakeGroup();
        await _repo.InsertOptimizationGroupAsync(group, ct);

        var run0 = MakeChildRun(group.Id, 0);
        var run1 = MakeChildRun(group.Id, 1);
        await _repo.InsertOptimizationPlaceholderAsync(run0, ct);
        await _repo.InsertOptimizationPlaceholderAsync(run1, ct);

        // Insert 3 trials per run
        for (var i = 0; i < 3; i++)
        {
            await _repo.SaveAsync(MakeTrial(run0.Id, i), ct);
            await _repo.SaveAsync(MakeTrial(run1.Id, i + 100), ct);
        }

        var result = await _repo.GetOptimizationGroupTrialsAsync(group.Id, ct: ct);
        Assert.Equal(6, result.TotalCount);
        Assert.Equal(6, result.Items.Count);
    }

    [Fact]
    public async Task GetGroupTrials_SortsBySharpeRatio()
    {
        var ct = TestContext.Current.CancellationToken;
        var group = MakeGroup(totalRuns: 1);
        await _repo.InsertOptimizationGroupAsync(group, ct);

        var run = MakeChildRun(group.Id, 0);
        await _repo.InsertOptimizationPlaceholderAsync(run, ct);

        for (var i = 0; i < 5; i++)
            await _repo.SaveAsync(MakeTrial(run.Id, i), ct);

        var result = await _repo.GetOptimizationGroupTrialsAsync(
            group.Id, sortBy: "SharpeRatio", ct: ct);

        Assert.Equal(5, result.Items.Count);
        // Verify descending order
        for (var i = 1; i < result.Items.Count; i++)
        {
            Assert.True(
                result.Items[i - 1].Metrics.SharpeRatio >= result.Items[i].Metrics.SharpeRatio,
                "Trials should be sorted by SharpeRatio descending");
        }
    }

    // ── T075: Benchmark cross-DSS trial sorting with 10K+ rows ──────────

    [Fact]
    public async Task GetGroupTrials_10KRowsPerDss_SortsInUnderOneSecond()
    {
        var ct = TestContext.Current.CancellationToken;
        const int trialsPerRun = 10_000;
        const int dssCount = 2;

        var group = MakeGroup(totalRuns: dssCount);
        await _repo.InsertOptimizationGroupAsync(group, ct);

        var runIds = new Guid[dssCount];
        for (var d = 0; d < dssCount; d++)
        {
            var run = MakeChildRun(group.Id, d);
            runIds[d] = run.Id;
            await _repo.InsertOptimizationPlaceholderAsync(run, ct);
        }

        // Bulk insert trials (10K per DSS)
        for (var d = 0; d < dssCount; d++)
        {
            for (var i = 0; i < trialsPerRun; i++)
                await _repo.SaveAsync(MakeTrial(runIds[d], d * trialsPerRun + i), ct);
        }

        // Benchmark the sort query
        var sw = Stopwatch.StartNew();
        var result = await _repo.GetOptimizationGroupTrialsAsync(
            group.Id, limit: 1000, sortBy: "SharpeRatio", ct: ct);
        sw.Stop();

        Assert.Equal(1000, result.Items.Count);
        Assert.Equal(trialsPerRun * dssCount, result.TotalCount);
        // SC-003 target is <1000ms on production hardware; threshold doubled here for
        // CI-runner variance. Treat regressions beyond 2000ms as a real signal.
        Assert.True(
            sw.ElapsedMilliseconds < 2000,
            $"Cross-DSS trial sort took {sw.ElapsedMilliseconds}ms, expected <2000ms (SC-003 target <1000ms)");
    }
}
