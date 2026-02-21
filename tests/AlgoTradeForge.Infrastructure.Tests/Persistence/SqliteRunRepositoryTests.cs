using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Persistence;

public class SqliteRunRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteRunRepository _repo;

    public SqliteRunRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"runs_test_{Guid.NewGuid():N}.sqlite");
        var options = Options.Create(new RunStorageOptions { DatabasePath = _dbPath });
        _repo = new SqliteRunRepository(options);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private static PerformanceMetrics MakeMetrics(decimal netProfit = 1000m) => new()
    {
        TotalTrades = 10,
        WinningTrades = 6,
        LosingTrades = 4,
        NetProfit = netProfit,
        GrossProfit = 2000m,
        GrossLoss = -1000m,
        TotalCommissions = 50m,
        TotalReturnPct = 10.0,
        AnnualizedReturnPct = 15.0,
        SharpeRatio = 1.5,
        SortinoRatio = 2.0,
        MaxDrawdownPct = -5.0,
        WinRatePct = 60.0,
        ProfitFactor = 2.0,
        AverageWin = 333.33,
        AverageLoss = -250.0,
        InitialCapital = 10000m,
        FinalEquity = 11000m,
        TradingDays = 252,
    };

    private static BacktestRunRecord MakeBacktestRecord(
        Guid? id = null,
        string strategyName = "ZigZagBreakout",
        string? runFolderPath = "/data/events/run1",
        Guid? optimizationRunId = null)
    {
        return new BacktestRunRecord
        {
            Id = id ?? Guid.NewGuid(),
            StrategyName = strategyName,
            StrategyVersion = "1.0.0",
            Parameters = new Dictionary<string, object> { ["DzzDepth"] = 5L, ["MinimumThreshold"] = 0.01m },
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            TimeFrame = "1h",
            InitialCash = 10000m,
            Commission = 0.1m,
            SlippageTicks = 2,
            StartedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 5, TimeSpan.Zero),
            DataStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DataEnd = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero),
            DurationMs = 5000,
            TotalBars = 8760,
            Metrics = MakeMetrics(),
            EquityCurve =
            [
                new EquityPoint(1704067200000, 10000m),
                new EquityPoint(1704070800000, 10100m),
                new EquityPoint(1704074400000, 10050m),
            ],
            RunFolderPath = runFolderPath,
            RunMode = "Backtest",
            OptimizationRunId = optimizationRunId,
        };
    }

    // ── Save + GetById round-trip ──────────────────────────────────────

    [Fact]
    public async Task SaveAndGetById_RoundTripsAllFields()
    {
        var original = MakeBacktestRecord();

        await _repo.SaveAsync(original);
        var loaded = await _repo.GetByIdAsync(original.Id);

        Assert.NotNull(loaded);
        Assert.Equal(original.Id, loaded.Id);
        Assert.Equal(original.StrategyName, loaded.StrategyName);
        Assert.Equal(original.StrategyVersion, loaded.StrategyVersion);
        Assert.Equal(original.InitialCash, loaded.InitialCash);
        Assert.Equal(original.Commission, loaded.Commission);
        Assert.Equal(original.SlippageTicks, loaded.SlippageTicks);
        Assert.Equal(original.DurationMs, loaded.DurationMs);
        Assert.Equal(original.TotalBars, loaded.TotalBars);
        Assert.Equal(original.RunFolderPath, loaded.RunFolderPath);
        Assert.Equal(original.RunMode, loaded.RunMode);
        Assert.Null(loaded.OptimizationRunId);

        // Metrics
        Assert.Equal(original.Metrics.TotalTrades, loaded.Metrics.TotalTrades);
        Assert.Equal(original.Metrics.NetProfit, loaded.Metrics.NetProfit);
        Assert.Equal(original.Metrics.SharpeRatio, loaded.Metrics.SharpeRatio);
        Assert.Equal(original.Metrics.InitialCapital, loaded.Metrics.InitialCapital);
        Assert.Equal(original.Metrics.FinalEquity, loaded.Metrics.FinalEquity);

        // Equity curve with timestamps
        Assert.Equal(original.EquityCurve.Count, loaded.EquityCurve.Count);
        for (var i = 0; i < original.EquityCurve.Count; i++)
        {
            Assert.Equal(original.EquityCurve[i].TimestampMs, loaded.EquityCurve[i].TimestampMs);
            Assert.Equal(original.EquityCurve[i].Value, loaded.EquityCurve[i].Value);
        }

        // Parameters
        Assert.Equal(5L, loaded.Parameters["DzzDepth"]);

        // Data subscription fields
        Assert.Equal("BTCUSDT", loaded.AssetName);
        Assert.Equal("Binance", loaded.Exchange);
        Assert.Equal("1h", loaded.TimeFrame);
    }

    // ── GetById returns null for non-existent ──────────────────────────

    [Fact]
    public async Task GetById_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    // ── Query by strategy name ─────────────────────────────────────────

    [Fact]
    public async Task Query_FiltersByStrategyName()
    {
        await _repo.SaveAsync(MakeBacktestRecord(strategyName: "ZigZagBreakout"));
        await _repo.SaveAsync(MakeBacktestRecord(strategyName: "MomentumStrategy"));

        var results = await _repo.QueryAsync(new BacktestRunQuery { StrategyName = "ZigZagBreakout" });

        Assert.Equal(1, results.TotalCount);
        Assert.Single(results.Items);
        Assert.Equal("ZigZagBreakout", results.Items[0].StrategyName);
    }

    // ── Query by asset + exchange ──────────────────────────────────────

    [Fact]
    public async Task Query_FiltersByAssetAndExchange()
    {
        var r1 = MakeBacktestRecord() with
        {
            Id = Guid.NewGuid(),
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            TimeFrame = "1h",
        };
        var r2 = MakeBacktestRecord() with
        {
            Id = Guid.NewGuid(),
            AssetName = "ETHUSDT",
            Exchange = "Binance",
            TimeFrame = "4h",
        };

        await _repo.SaveAsync(r1);
        await _repo.SaveAsync(r2);

        var results = await _repo.QueryAsync(new BacktestRunQuery { AssetName = "BTCUSDT" });
        Assert.Single(results.Items);
        Assert.Equal("BTCUSDT", results.Items[0].AssetName);
    }

    // ── Query by timeframe ─────────────────────────────────────────────

    [Fact]
    public async Task Query_FiltersByTimeFrame()
    {
        var r1 = MakeBacktestRecord() with
        {
            Id = Guid.NewGuid(),
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            TimeFrame = "1h",
        };
        var r2 = MakeBacktestRecord() with
        {
            Id = Guid.NewGuid(),
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            TimeFrame = "4h",
        };

        await _repo.SaveAsync(r1);
        await _repo.SaveAsync(r2);

        var results = await _repo.QueryAsync(new BacktestRunQuery { TimeFrame = "4h" });
        Assert.Single(results.Items);
        Assert.Equal("4h", results.Items[0].TimeFrame);
    }

    // ── Query by date range ────────────────────────────────────────────

    [Fact]
    public async Task Query_FiltersByDateRange()
    {
        var early = MakeBacktestRecord() with
        {
            Id = Guid.NewGuid(),
            CompletedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var late = MakeBacktestRecord() with
        {
            Id = Guid.NewGuid(),
            CompletedAt = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };

        await _repo.SaveAsync(early);
        await _repo.SaveAsync(late);

        var results = await _repo.QueryAsync(new BacktestRunQuery
        {
            From = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero),
        });

        Assert.Single(results.Items);
        Assert.Equal(late.Id, results.Items[0].Id);
    }

    // ── Query pagination ───────────────────────────────────────────────

    [Fact]
    public async Task Query_Pagination_RespectsLimitAndOffset()
    {
        for (var i = 0; i < 5; i++)
        {
            var r = MakeBacktestRecord() with
            {
                Id = Guid.NewGuid(),
                CompletedAt = new DateTimeOffset(2025, 1, 1 + i, 0, 0, 0, TimeSpan.Zero),
            };
            await _repo.SaveAsync(r);
        }

        var page1 = await _repo.QueryAsync(new BacktestRunQuery { Limit = 2, Offset = 0 });
        var page2 = await _repo.QueryAsync(new BacktestRunQuery { Limit = 2, Offset = 2 });

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(2, page2.Items.Count);
        Assert.NotEqual(page1.Items[0].Id, page2.Items[0].Id);
    }

    // ── Query chronological order ──────────────────────────────────────

    [Fact]
    public async Task Query_ReturnsChronologicalDescOrder()
    {
        var r1 = MakeBacktestRecord() with
        {
            Id = Guid.NewGuid(),
            CompletedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        };
        var r2 = MakeBacktestRecord() with
        {
            Id = Guid.NewGuid(),
            CompletedAt = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
        };

        await _repo.SaveAsync(r1);
        await _repo.SaveAsync(r2);

        var results = await _repo.QueryAsync(new BacktestRunQuery());

        Assert.Equal(2, results.Items.Count);
        Assert.Equal(r2.Id, results.Items[0].Id); // Later first
        Assert.Equal(r1.Id, results.Items[1].Id);
    }

    // ── Save optimization + children ───────────────────────────────────

    [Fact]
    public async Task SaveOptimization_PersistsParentAndChildren()
    {
        var optId = Guid.NewGuid();
        var trial1 = MakeBacktestRecord(optimizationRunId: optId, runFolderPath: null) with
        {
            Parameters = new Dictionary<string, object> { ["DzzDepth"] = 3L },
        };
        var trial2 = MakeBacktestRecord(optimizationRunId: optId, runFolderPath: null) with
        {
            Id = Guid.NewGuid(),
            Parameters = new Dictionary<string, object> { ["DzzDepth"] = 5L },
        };

        var optRecord = new OptimizationRunRecord
        {
            Id = optId,
            StrategyName = "ZigZagBreakout",
            StrategyVersion = "1.0.0",
            StartedAt = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CompletedAt = new DateTimeOffset(2025, 1, 1, 0, 1, 0, TimeSpan.Zero),
            DurationMs = 60000,
            TotalCombinations = 2,
            SortBy = "SharpeRatio",
            DataStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DataEnd = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero),
            InitialCash = 10000m,
            Commission = 0.1m,
            SlippageTicks = 2,
            MaxParallelism = 4,
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            TimeFrame = "1h",
            Trials = [trial1, trial2],
        };

        await _repo.SaveOptimizationAsync(optRecord);

        var loaded = await _repo.GetOptimizationByIdAsync(optId);

        Assert.NotNull(loaded);
        Assert.Equal(optId, loaded.Id);
        Assert.Equal("ZigZagBreakout", loaded.StrategyName);
        Assert.Equal("1.0.0", loaded.StrategyVersion);
        Assert.Equal(2, loaded.TotalCombinations);
        Assert.Equal("SharpeRatio", loaded.SortBy);
        Assert.Equal(10000m, loaded.InitialCash);
        Assert.Equal(4, loaded.MaxParallelism);
        Assert.Equal(2, loaded.Trials.Count);

        // Verify optimization data subscription fields
        Assert.Equal("BTCUSDT", loaded.AssetName);
        Assert.Equal("Binance", loaded.Exchange);
        Assert.Equal("1h", loaded.TimeFrame);
    }

    // ── Get optimization by ID with all trials ─────────────────────────

    [Fact]
    public async Task GetOptimizationById_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetOptimizationByIdAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    // ── Distinct strategy names ────────────────────────────────────────

    [Fact]
    public async Task GetDistinctStrategyNames_ReturnsUniqueNamesAcrossBothTables()
    {
        // Save a backtest with strategy "Alpha"
        await _repo.SaveAsync(MakeBacktestRecord(strategyName: "Alpha"));

        // Save an optimization with strategy "Beta"
        var optId = Guid.NewGuid();
        var trial = MakeBacktestRecord(strategyName: "Beta", optimizationRunId: optId, runFolderPath: null);
        var opt = new OptimizationRunRecord
        {
            Id = optId,
            StrategyName = "Beta",
            StrategyVersion = "1.0.0",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 100,
            TotalCombinations = 1,
            SortBy = "SharpeRatio",
            DataStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DataEnd = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero),
            InitialCash = 10000m,
            Commission = 0m,
            SlippageTicks = 0,
            MaxParallelism = 1,
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            TimeFrame = "1h",
            Trials = [trial],
        };
        await _repo.SaveOptimizationAsync(opt);

        // Save another backtest with "Alpha" (duplicate)
        await _repo.SaveAsync(MakeBacktestRecord(id: Guid.NewGuid(), strategyName: "Alpha"));

        var names = await _repo.GetDistinctStrategyNamesAsync();

        Assert.Equal(2, names.Count);
        Assert.Contains("Alpha", names);
        Assert.Contains("Beta", names);
    }

    // ── StandaloneOnly filter ─────────────────────────────────────────

    [Fact]
    public async Task Query_StandaloneOnly_ExcludesOptimizationTrials()
    {
        var optId = Guid.NewGuid();

        // Standalone backtest
        var standalone = MakeBacktestRecord();
        await _repo.SaveAsync(standalone);

        // Optimization with one trial
        var trial = MakeBacktestRecord(optimizationRunId: optId, runFolderPath: null) with
        {
            Id = Guid.NewGuid(),
        };
        var opt = new OptimizationRunRecord
        {
            Id = optId,
            StrategyName = "ZigZagBreakout",
            StrategyVersion = "1.0.0",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 100,
            TotalCombinations = 1,
            SortBy = "SharpeRatio",
            DataStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DataEnd = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero),
            InitialCash = 10000m,
            Commission = 0m,
            SlippageTicks = 0,
            MaxParallelism = 1,
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            TimeFrame = "1h",
            Trials = [trial],
        };
        await _repo.SaveOptimizationAsync(opt);

        // Without filter: both rows visible
        var all = await _repo.QueryAsync(new BacktestRunQuery());
        Assert.Equal(2, all.Items.Count);

        // With StandaloneOnly: only the standalone run
        var standaloneOnly = await _repo.QueryAsync(new BacktestRunQuery { StandaloneOnly = true });
        Assert.Single(standaloneOnly.Items);
        Assert.Equal(standalone.Id, standaloneOnly.Items[0].Id);
        Assert.Null(standaloneOnly.Items[0].OptimizationRunId);
    }

    // ── Limit capping ─────────────────────────────────────────────────

    [Fact]
    public void Limit_ClampedToMaxLimit()
    {
        var query = new BacktestRunQuery { Limit = 10_000 };
        Assert.Equal(BacktestRunQuery.MaxLimit, query.Limit);
    }

    [Fact]
    public void Limit_ClampedToMinimumOne()
    {
        var query = new BacktestRunQuery { Limit = -5 };
        Assert.Equal(1, query.Limit);
    }

    [Fact]
    public void OptimizationLimit_ClampedToMaxLimit()
    {
        var query = new OptimizationRunQuery { Limit = 10_000 };
        Assert.Equal(OptimizationRunQuery.MaxLimit, query.Limit);
    }

    // ── Offset clamping ───────────────────────────────────────────────

    [Fact]
    public void Offset_NegativeClampedToZero()
    {
        var query = new BacktestRunQuery { Offset = -10 };
        Assert.Equal(0, query.Offset);
    }

    [Fact]
    public void OptimizationOffset_NegativeClampedToZero()
    {
        var query = new OptimizationRunQuery { Offset = -10 };
        Assert.Equal(0, query.Offset);
    }

    // ── Query list excludes equity curve ──────────────────────────────

    [Fact]
    public async Task Query_ReturnsEmptyEquityCurve_ForListResults()
    {
        var original = MakeBacktestRecord();
        await _repo.SaveAsync(original);

        var results = await _repo.QueryAsync(new BacktestRunQuery());

        Assert.Single(results.Items);
        Assert.Empty(results.Items[0].EquityCurve);

        // GetById still returns full equity curve
        var detail = await _repo.GetByIdAsync(original.Id);
        Assert.NotNull(detail);
        Assert.Equal(original.EquityCurve.Count, detail.EquityCurve.Count);
    }

    // ── TotalCount reflects full filtered count ───────────────────────

    [Fact]
    public async Task Query_TotalCount_ReflectsFullFilteredCount_RegardlessOfLimitOffset()
    {
        for (var i = 0; i < 5; i++)
        {
            await _repo.SaveAsync(MakeBacktestRecord() with
            {
                Id = Guid.NewGuid(),
                StrategyName = "Target",
                CompletedAt = new DateTimeOffset(2025, 1, 1 + i, 0, 0, 0, TimeSpan.Zero),
            });
        }
        await _repo.SaveAsync(MakeBacktestRecord(strategyName: "Other"));

        var page = await _repo.QueryAsync(new BacktestRunQuery
        {
            StrategyName = "Target",
            Limit = 2,
            Offset = 1,
        });

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }

    // ── Optimization TotalCount ───────────────────────────────────────

    [Fact]
    public async Task QueryOptimizations_TotalCount_ReflectsFilteredCount()
    {
        for (var i = 0; i < 3; i++)
        {
            var optId = Guid.NewGuid();
            var trial = MakeBacktestRecord(optimizationRunId: optId, runFolderPath: null) with { Id = Guid.NewGuid() };
            await _repo.SaveOptimizationAsync(new OptimizationRunRecord
            {
                Id = optId,
                StrategyName = "ZigZagBreakout",
                StrategyVersion = "1.0.0",
                StartedAt = new DateTimeOffset(2025, 1, 1 + i, 0, 0, 0, TimeSpan.Zero),
                CompletedAt = new DateTimeOffset(2025, 1, 1 + i, 0, 1, 0, TimeSpan.Zero),
                DurationMs = 1000,
                TotalCombinations = 1,
                SortBy = "SharpeRatio",
                DataStart = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
                DataEnd = new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero),
                InitialCash = 10000m,
                Commission = 0m,
                SlippageTicks = 0,
                MaxParallelism = 1,
                AssetName = "BTCUSDT",
                Exchange = "Binance",
                TimeFrame = "1h",
                Trials = [trial],
            });
        }

        var page = await _repo.QueryOptimizationsAsync(new OptimizationRunQuery { Limit = 2, Offset = 0 });

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }
}
