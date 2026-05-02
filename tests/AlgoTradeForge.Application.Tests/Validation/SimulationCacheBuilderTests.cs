using AlgoTradeForge.Application.Persistence;
using Xunit;
using AlgoTradeForge.Application.Validation;
using AlgoTradeForge.Domain.Reporting;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;

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
        Assert.Equal(3, cache.MaxBarCount);

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

        Assert.Equal(new long[] { 1000, 2000 }, cache.GetTrialTimestamps(0).ToArray());
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
    public void Build_VariableLengthCurves_Succeeds()
    {
        var trials = new List<BacktestRunRecord>
        {
            CreateTrial(10000m, [(100, 10050m), (200, 10100m)]),
            CreateTrial(10000m, [(100, 10050m)]), // only 1 point
        };

        var cache = SimulationCacheBuilder.Build(trials);

        Assert.Equal(2, cache.TrialCount);
        Assert.Equal(2, cache.MaxBarCount);
        Assert.Equal(2, cache.GetBarCount(0));
        Assert.Equal(1, cache.GetBarCount(1));
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

    [Fact]
    public void BuildSubscriptionGroupMap_SingleSubscription_ReturnsNull()
    {
        var trials = new List<BacktestRunRecord>
        {
            CreateTrial(10000m, [(100, 10050m)]),
            CreateTrial(10000m, [(100, 10050m)]),
        };

        var result = SimulationCacheBuilder.BuildSubscriptionGroupMap(trials);

        Assert.Null(result);
    }

    [Fact]
    public void BuildSubscriptionGroupMap_MultipleSubscriptions_ReturnsDictWithGroupKeys()
    {
        var trial1 = CreateTrial(10000m, [(100, 10050m)]);
        var trial2 = CreateTrialWithSubscription(10000m, [(100, 10050m)], "ETHUSD", "binance", "1h");

        var trials = new List<BacktestRunRecord> { trial1, trial2 };

        var result = SimulationCacheBuilder.BuildSubscriptionGroupMap(trials);

        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
        // Phase 4 P4-A: BacktestInputsFormatter.Key includes the Role segment as an integer
        // ordinal (Primary=0, Side=1) so the format is decoupled from JSON enum serialization.
        Assert.Equal("BTCUSDT:binance:1h:0", result[0]);
        Assert.Equal("ETHUSD:binance:1h:0", result[1]);
    }

    [Fact]
    public void BuildSubscriptionGroupMap_MultiSubscriptionTrial_UsesAllSubscriptionsSorted()
    {
        var trial1 = CreateTrial(10000m, [(100, 10050m)]);
        var trial2 = CreateTrial(10000m, [(100, 10050m)]);
        // Give trial2 two subscriptions (pairs-style) — intentionally unsorted
        trial2 = trial2 with
        {
            DataSubscriptions =
            [
                new TimeBarSubscription("ETHUSD", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")),
                new TimeBarSubscription("BTCUSD", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h")),
            ],
        };

        var result = SimulationCacheBuilder.BuildSubscriptionGroupMap([trial1, trial2]);

        Assert.NotNull(result);
        // Phase 4 P4-A: Key format is asset:exchange:feed:role(int)
        Assert.Equal("BTCUSDT:binance:1h:0", result[0]);
        // Sorted: BTCUSD comes before ETHUSD
        Assert.Equal("BTCUSD:binance:1h:0,ETHUSD:binance:1h:0", result[1]);
    }

    [Fact]
    public void BuildSubscriptionGroupMap_Empty_ReturnsNull()
    {
        var result = SimulationCacheBuilder.BuildSubscriptionGroupMap([]);
        Assert.Null(result);
    }

    private static BacktestRunRecord CreateTrialWithSubscription(
        decimal initialCapital,
        (long timestamp, decimal value)[] equityPoints,
        string assetName, string exchange, string timeFrame)
    {
        var trial = CreateTrial(initialCapital, equityPoints);
        return trial with
        {
            DataSubscriptions = [new TimeBarSubscription(assetName, exchange, DataFeedRole.Primary, TimeFrame.Parse(timeFrame))],
        };
    }

    private static BacktestRunRecord CreateTrial(decimal initialCapital, (long timestamp, decimal value)[] equityPoints)
    {
        return new BacktestRunRecord
        {
            Id = Guid.NewGuid(),
            StrategyName = "Test",
            StrategyVersion = "1.0",
            Parameters = new Dictionary<string, object>(),
            DataSubscriptions = [new TimeBarSubscription("BTCUSDT", "binance", DataFeedRole.Primary, TimeFrame.Parse("1h"))],
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
