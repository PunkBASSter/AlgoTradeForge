using AlgoTradeForge.Application.Persistence;
using Xunit;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Reporting;

namespace AlgoTradeForge.Application.Tests.Validation;

public class SimulationCacheBuilderTests
{
    [Fact]
    public void Build_ComputesDeltasCorrectly()
    {
        var trials = new List<BacktestRunRecord>
        {
            CreateTrial(10000m, [(100, 10000m), (200, 10050m), (300, 10120m)]),
            CreateTrial(10000m, [(100, 10000m), (200, 9980m), (300, 10010m)]),
        };

        var cache = SimulationCacheBuilder.Build(trials);

        Assert.Equal(2, cache.TrialCount);
        Assert.Equal(3, cache.BarCount);

        // Trial 0: deltas = [10000-10000, 10050-10000, 10120-10050] = [0, 50, 70]
        var row0 = cache.GetTrialPnl(0);
        Assert.Equal(0.0, row0[0]);
        Assert.Equal(50.0, row0[1]);
        Assert.Equal(70.0, row0[2]);

        // Trial 1: deltas = [0, -20, 30]
        var row1 = cache.GetTrialPnl(1);
        Assert.Equal(0.0, row1[0]);
        Assert.Equal(-20.0, row1[1]);
        Assert.Equal(30.0, row1[2]);
    }

    [Fact]
    public void Build_ExtractsTimestamps()
    {
        var trials = new List<BacktestRunRecord>
        {
            CreateTrial(10000m, [(1000, 10100m), (2000, 10200m)]),
        };

        var cache = SimulationCacheBuilder.Build(trials);

        Assert.Equal(new long[] { 1000, 2000 }, cache.BarTimestamps);
    }

    [Fact]
    public void Build_EmptyTrials_Throws()
    {
        Assert.Throws<ArgumentException>(() => SimulationCacheBuilder.Build([]));
    }

    [Fact]
    public void Build_EmptyEquityCurve_Throws()
    {
        var trials = new List<BacktestRunRecord>
        {
            CreateTrial(10000m, []),
        };

        Assert.Throws<ArgumentException>(() => SimulationCacheBuilder.Build(trials));
    }

    [Fact]
    public void Build_MismatchedCurveLengths_Throws()
    {
        var trials = new List<BacktestRunRecord>
        {
            CreateTrial(10000m, [(100, 10050m), (200, 10100m)]),
            CreateTrial(10000m, [(100, 10050m)]), // only 1 point
        };

        Assert.Throws<ArgumentException>(() => SimulationCacheBuilder.Build(trials));
    }

    [Fact]
    public void Build_InitialCapitalDelta_Captured()
    {
        // If equity starts at 10100 but initial capital is 10000, delta[0] = 100
        var trials = new List<BacktestRunRecord>
        {
            CreateTrial(10000m, [(100, 10100m), (200, 10200m)]),
        };

        var cache = SimulationCacheBuilder.Build(trials);

        Assert.Equal(100.0, cache.GetTrialPnl(0)[0]); // 10100 - 10000
        Assert.Equal(100.0, cache.GetTrialPnl(0)[1]); // 10200 - 10100
    }

    [Fact]
    public void BuildTrialSummaries_MapsCorrectly()
    {
        var trials = new List<BacktestRunRecord>
        {
            CreateTrial(10000m, [(100, 10050m), (200, 10100m)]),
        };

        var summaries = SimulationCacheBuilder.BuildTrialSummaries(trials);

        Assert.Single(summaries);
        Assert.Equal(0, summaries[0].Index);
        Assert.Equal(trials[0].Id, summaries[0].Id);
        Assert.Equal(trials[0].Metrics.NetProfit, summaries[0].Metrics.NetProfit);
    }

    private static BacktestRunRecord CreateTrial(decimal initialCapital, (long timestamp, decimal value)[] equityPoints)
    {
        return new BacktestRunRecord
        {
            Id = Guid.NewGuid(),
            StrategyName = "Test",
            StrategyVersion = "1.0",
            Parameters = new Dictionary<string, object>(),
            DataSubscription = new DataSubscriptionDto
            {
                AssetName = "BTCUSDT",
                Exchange = "binance",
                TimeFrame = "1h",
            },
            BacktestSettings = new BacktestSettingsDto
            {
                InitialCash = initialCapital,
                StartTime = DateTimeOffset.UtcNow.AddDays(-30),
                EndTime = DateTimeOffset.UtcNow,
                CommissionPerTrade = 0.001m,
                SlippageTicks = 1,
            },
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            DurationMs = 100,
            TotalBars = equityPoints.Length,
            Metrics = new PerformanceMetrics
            {
                TotalTrades = 50,
                WinningTrades = 30,
                LosingTrades = 20,
                NetProfit = equityPoints.Length > 0 ? equityPoints[^1].value - initialCapital : 0m,
                GrossProfit = 1000m,
                GrossLoss = -500m,
                TotalCommissions = 10m,
                TotalReturnPct = 5.0,
                AnnualizedReturnPct = 10.0,
                SharpeRatio = 1.5,
                SortinoRatio = 2.0,
                MaxDrawdownPct = 10.0,
                WinRatePct = 60.0,
                ProfitFactor = 2.0,
                AverageWin = 50.0,
                AverageLoss = -25.0,
                InitialCapital = initialCapital,
                FinalEquity = equityPoints.Length > 0 ? equityPoints[^1].value : initialCapital,
                TradingDays = 30,
            },
            EquityCurve = equityPoints.Select(p => new EquityPoint(p.timestamp, (double)p.value)).ToList(),
            TradePnl = [],
            RunMode = RunModes.Backtest,
        };
    }
}
