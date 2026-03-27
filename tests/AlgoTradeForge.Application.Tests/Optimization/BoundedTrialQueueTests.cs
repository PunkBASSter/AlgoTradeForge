using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Domain.Optimization.Fitness;
using AlgoTradeForge.Domain.Reporting;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public sealed class BoundedTrialQueueTests
{
    private static BacktestRunRecord MakeRecord(double sharpe = 0, double sortino = 0,
        double profitFactor = 0, double maxDrawdownPct = 0, decimal netProfit = 0)
    {
        return new BacktestRunRecord
        {
            Id = Guid.NewGuid(),
            StrategyName = "Test",
            StrategyVersion = "1",
            Parameters = new Dictionary<string, object> { ["sharpe"] = sharpe },
            DataSubscription = new DataSubscriptionDto { AssetName = "BTCUSDT", Exchange = "Binance", TimeFrame = "1h" },
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = 10_000m,
                CommissionPerTrade = 0m,
                SlippageTicks = 0,
                StartTime = DateTimeOffset.UtcNow.AddDays(-30),
                EndTime = DateTimeOffset.UtcNow,
            },
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 100,
            TotalBars = 720,
            Metrics = new PerformanceMetrics
            {
                TotalTrades = 10, WinningTrades = 6, LosingTrades = 4,
                NetProfit = netProfit, GrossProfit = 500m, GrossLoss = -200m, TotalCommissions = 0m,
                TotalReturnPct = 3.0, AnnualizedReturnPct = 36.0,
                SharpeRatio = sharpe, SortinoRatio = sortino, MaxDrawdownPct = maxDrawdownPct,
                WinRatePct = 60.0, ProfitFactor = profitFactor, AverageWin = 83.3, AverageLoss = -50.0,
                InitialCapital = 10_000m, FinalEquity = 10_300m, TradingDays = 30,
            },
            EquityCurve = [],
            RunMode = RunModes.Backtest,
        };
    }

    [Fact]
    public void Fills_up_to_capacity()
    {
        var queue = new BoundedTrialQueue(3, MetricNames.SharpeRatio);

        queue.TryAdd(MakeRecord(sharpe: 1.0));
        queue.TryAdd(MakeRecord(sharpe: 2.0));
        queue.TryAdd(MakeRecord(sharpe: 3.0));

        var results = queue.DrainSorted();
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void Evicts_worst_when_full_and_better_arrives()
    {
        var queue = new BoundedTrialQueue(2, MetricNames.SharpeRatio);

        queue.TryAdd(MakeRecord(sharpe: 1.0));
        queue.TryAdd(MakeRecord(sharpe: 2.0));
        queue.TryAdd(MakeRecord(sharpe: 3.0)); // should evict 1.0

        var results = queue.DrainSorted();
        Assert.Equal(2, results.Count);
        Assert.Equal(3.0, results[0].Metrics.SharpeRatio);
        Assert.Equal(2.0, results[1].Metrics.SharpeRatio);
    }

    [Fact]
    public void Rejects_worse_item_when_full()
    {
        var queue = new BoundedTrialQueue(2, MetricNames.SharpeRatio);

        queue.TryAdd(MakeRecord(sharpe: 2.0));
        queue.TryAdd(MakeRecord(sharpe: 3.0));
        queue.TryAdd(MakeRecord(sharpe: 1.0)); // worse than both, rejected

        var results = queue.DrainSorted();
        Assert.Equal(2, results.Count);
        Assert.Equal(3.0, results[0].Metrics.SharpeRatio);
        Assert.Equal(2.0, results[1].Metrics.SharpeRatio);
    }

    [Fact]
    public void DrainSorted_returns_best_first()
    {
        var queue = new BoundedTrialQueue(5, MetricNames.SharpeRatio);

        queue.TryAdd(MakeRecord(sharpe: 1.5));
        queue.TryAdd(MakeRecord(sharpe: 3.0));
        queue.TryAdd(MakeRecord(sharpe: 0.5));
        queue.TryAdd(MakeRecord(sharpe: 2.0));

        var results = queue.DrainSorted();
        Assert.Equal(4, results.Count);
        Assert.Equal(3.0, results[0].Metrics.SharpeRatio);
        Assert.Equal(2.0, results[1].Metrics.SharpeRatio);
        Assert.Equal(1.5, results[2].Metrics.SharpeRatio);
        Assert.Equal(0.5, results[3].Metrics.SharpeRatio);
    }

    [Fact]
    public void Ascending_mode_keeps_lowest_drawdown()
    {
        var queue = new BoundedTrialQueue(2, MetricNames.MaxDrawdownPct);

        queue.TryAdd(MakeRecord(maxDrawdownPct: 30.0));
        queue.TryAdd(MakeRecord(maxDrawdownPct: 10.0));
        queue.TryAdd(MakeRecord(maxDrawdownPct: 5.0)); // should evict 30.0

        var results = queue.DrainSorted();
        Assert.Equal(2, results.Count);
        // Best-first for drawdown = lowest first
        Assert.Equal(5.0, results[0].Metrics.MaxDrawdownPct);
        Assert.Equal(10.0, results[1].Metrics.MaxDrawdownPct);
    }

    [Fact]
    public void Ascending_mode_rejects_higher_drawdown_when_full()
    {
        var queue = new BoundedTrialQueue(2, MetricNames.MaxDrawdownPct);

        queue.TryAdd(MakeRecord(maxDrawdownPct: 5.0));
        queue.TryAdd(MakeRecord(maxDrawdownPct: 10.0));
        queue.TryAdd(MakeRecord(maxDrawdownPct: 50.0)); // worse, rejected

        var results = queue.DrainSorted();
        Assert.Equal(2, results.Count);
        Assert.Equal(5.0, results[0].Metrics.MaxDrawdownPct);
        Assert.Equal(10.0, results[1].Metrics.MaxDrawdownPct);
    }

    [Fact]
    public void SortBy_NetProfit_keeps_highest()
    {
        var queue = new BoundedTrialQueue(2, MetricNames.NetProfit);

        queue.TryAdd(MakeRecord(netProfit: 100m));
        queue.TryAdd(MakeRecord(netProfit: 300m));
        queue.TryAdd(MakeRecord(netProfit: 200m)); // should evict 100

        var results = queue.DrainSorted();
        Assert.Equal(2, results.Count);
        Assert.Equal(300m, results[0].Metrics.NetProfit);
        Assert.Equal(200m, results[1].Metrics.NetProfit);
    }

    [Fact]
    public void SortBy_ProfitFactor_keeps_highest()
    {
        var queue = new BoundedTrialQueue(2, MetricNames.ProfitFactor);

        queue.TryAdd(MakeRecord(profitFactor: 1.0));
        queue.TryAdd(MakeRecord(profitFactor: 2.5));
        queue.TryAdd(MakeRecord(profitFactor: 3.0));

        var results = queue.DrainSorted();
        Assert.Equal(2, results.Count);
        Assert.Equal(3.0, results[0].Metrics.ProfitFactor);
        Assert.Equal(2.5, results[1].Metrics.ProfitFactor);
    }

    [Fact]
    public void Unknown_sortBy_defaults_to_SharpeRatio()
    {
        var queue = new BoundedTrialQueue(2, "UnknownMetric");

        queue.TryAdd(MakeRecord(sharpe: 1.0));
        queue.TryAdd(MakeRecord(sharpe: 3.0));
        queue.TryAdd(MakeRecord(sharpe: 2.0));

        var results = queue.DrainSorted();
        Assert.Equal(3.0, results[0].Metrics.SharpeRatio);
        Assert.Equal(2.0, results[1].Metrics.SharpeRatio);
    }

    [Fact]
    public void Capacity_one_keeps_single_best()
    {
        var queue = new BoundedTrialQueue(1, MetricNames.SharpeRatio);

        queue.TryAdd(MakeRecord(sharpe: 1.0));
        queue.TryAdd(MakeRecord(sharpe: 5.0));
        queue.TryAdd(MakeRecord(sharpe: 3.0));

        var results = queue.DrainSorted();
        Assert.Single(results);
        Assert.Equal(5.0, results[0].Metrics.SharpeRatio);
    }

    [Fact]
    public void DrainSorted_returns_empty_when_nothing_added()
    {
        var queue = new BoundedTrialQueue(10, MetricNames.SharpeRatio);
        var results = queue.DrainSorted();
        Assert.Empty(results);
    }

    [Fact]
    public void Fitness_function_ranks_by_composite_score()
    {
        var fitnessFunction = new CompositeFitnessFunction();
        var queue = new BoundedTrialQueue(3, fitnessFunction);

        // Good all-around: moderate Sharpe, decent Sortino, good PF
        var balanced = MakeRecord(sharpe: 1.5, sortino: 2.0, profitFactor: 2.5);
        // One-dimensional: great Sharpe but nothing else
        var sharpeOnly = MakeRecord(sharpe: 3.0, sortino: 0.5, profitFactor: 1.1);
        // Poor overall
        var weak = MakeRecord(sharpe: 0.3, sortino: 0.2, profitFactor: 0.8);

        queue.TryAdd(weak);
        queue.TryAdd(balanced);
        queue.TryAdd(sharpeOnly);

        var results = queue.DrainSorted();
        Assert.Equal(3, results.Count);
        // All three should be ranked — verify best-first ordering via composite
        var fitness0 = fitnessFunction.Evaluate(results[0].Metrics);
        var fitness1 = fitnessFunction.Evaluate(results[1].Metrics);
        var fitness2 = fitnessFunction.Evaluate(results[2].Metrics);
        Assert.True(fitness0 >= fitness1, $"First ({fitness0}) should be >= second ({fitness1})");
        Assert.True(fitness1 >= fitness2, $"Second ({fitness1}) should be >= third ({fitness2})");
    }

    [Fact]
    public void Fitness_function_penalizes_high_drawdown()
    {
        var fitnessFunction = new CompositeFitnessFunction();
        var queue = new BoundedTrialQueue(2, fitnessFunction);

        // Great Sharpe but extreme drawdown
        var highDdRecord = MakeRecord(sharpe: 3.0, sortino: 3.0, profitFactor: 3.0, maxDrawdownPct: 80.0);
        // Moderate metrics, reasonable drawdown
        var balancedRecord = MakeRecord(sharpe: 1.5, sortino: 1.5, profitFactor: 2.0, maxDrawdownPct: 15.0);

        queue.TryAdd(highDdRecord);
        queue.TryAdd(balancedRecord);

        var results = queue.DrainSorted();
        // Balanced trial should rank first despite lower raw metrics, due to DD penalty
        Assert.Equal(15.0, results[0].Metrics.MaxDrawdownPct);
        Assert.Equal(80.0, results[1].Metrics.MaxDrawdownPct);
    }
}
