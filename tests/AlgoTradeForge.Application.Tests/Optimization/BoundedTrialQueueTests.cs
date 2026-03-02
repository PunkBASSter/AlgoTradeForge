using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Application.Persistence;
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
            AssetName = "BTCUSDT",
            Exchange = "Binance",
            TimeFrame = "1h",
            InitialCash = 10_000m,
            Commission = 0m,
            SlippageTicks = 0,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DataStart = DateTimeOffset.UtcNow.AddDays(-30),
            DataEnd = DateTimeOffset.UtcNow,
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
}
