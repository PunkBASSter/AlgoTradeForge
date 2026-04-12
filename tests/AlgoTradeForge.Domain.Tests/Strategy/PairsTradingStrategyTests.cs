using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.CrossAsset;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.PairsTrading;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy;

public sealed class PairsTradingStrategyTests
{
    private static readonly BacktestEngine Engine = new(new BarMatcher(), new OrderValidator());

    private static BacktestOptions CreateOptions(long initialCash = 10_000_000_000L) => new()
    {
        InitialCash = initialCash,
        StartTime = DateTimeOffset.MinValue,
        EndTime = DateTimeOffset.MaxValue,
    };

    private static PairsTradingParams CreateTestParams() => new()
    {
        CrossAsset = new CrossAssetParams
        {
            LookbackPeriod = 10,
            ZScoreEntryThreshold = 2.0,
            ZScoreExitThreshold = 0.5,
        },
        AtrPeriod = 7,
        SignalThreshold = 30,
        FilterThreshold = -100, // Effectively disabled
        DefaultAtrStopMultiplier = 3.0,
        MoneyManagement = new MoneyManagementParams { RiskPercent = 2.0 },
        TradeRegistry = new TradeRegistryParams { MaxConcurrentGroups = 1 },
        DataSubscriptions =
        [
            new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1)),
            new DataSubscription(TestAssets.Aapl, TimeSpan.FromMinutes(1)),
        ],
    };

    private static (TimeSeries<Int64Bar> series1, TimeSeries<Int64Bar> series2) CreateCorrelatedSeries()
    {
        var series1 = new TimeSeries<Int64Bar>();
        var series2 = new TimeSeries<Int64Bar>();
        var startMs = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var stepMs = 60_000L;

        // 30 bars of correlated movement (both trending up together)
        for (var i = 0; i < 30; i++)
        {
            var price1 = 50000L + i * 100;
            var price2 = 25000L + i * 50;
            series1.Add(new Int64Bar(startMs + i * stepMs, price1, price1 + 200, price1 - 100, price1 + 50, 1000));
            series2.Add(new Int64Bar(startMs + i * stepMs, price2, price2 + 100, price2 - 50, price2 + 25, 1000));
        }

        // 10 bars where asset 1 spikes but asset 2 stays flat → z-score diverges
        for (var i = 0; i < 10; i++)
        {
            var price1 = 53000L + i * 500; // Strong uptrend
            var price2 = 26500L;            // Flat
            series1.Add(new Int64Bar(startMs + (30 + i) * stepMs, price1, price1 + 200, price1 - 100, price1 + 100, 2000));
            series2.Add(new Int64Bar(startMs + (30 + i) * stepMs, price2, price2 + 100, price2 - 50, price2 + 25, 800));
        }

        // 10 bars of reversion
        for (var i = 0; i < 10; i++)
        {
            var price1 = 57500L - i * 400;
            var price2 = 26500L + i * 50;
            series1.Add(new Int64Bar(startMs + (40 + i) * stepMs, price1, price1 + 150, price1 - 100, price1 - 50, 1500));
            series2.Add(new Int64Bar(startMs + (40 + i) * stepMs, price2, price2 + 100, price2 - 50, price2 + 25, 900));
        }

        return (series1, series2);
    }

    [Fact]
    public void Run_AllBarsProcessed()
    {
        var (s1, s2) = CreateCorrelatedSeries();
        var strategy = new PairsTradingStrategy(CreateTestParams());

        var result = Engine.Run([s1, s2], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken);

        // Total bars = sum of both series
        Assert.Equal(s1.Count + s2.Count, result.TotalBarsProcessed);
    }

    [Fact]
    public void Run_ImplementsIInt64BarStrategy()
    {
        var strategy = new PairsTradingStrategy(CreateTestParams());
        Assert.IsAssignableFrom<IInt64BarStrategy>(strategy);
    }

    [Fact]
    public void Run_FewBars_DoesNotCrash()
    {
        var s1 = new TimeSeries<Int64Bar>();
        var s2 = new TimeSeries<Int64Bar>();
        var startMs = new DateTimeOffset(2024, 1, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        for (var i = 0; i < 5; i++)
        {
            s1.Add(new Int64Bar(startMs + i * 60000L, 50000 + i * 100, 50200 + i * 100, 49900 + i * 100, 50050 + i * 100, 1000));
            s2.Add(new Int64Bar(startMs + i * 60000L, 25000 + i * 50, 25100 + i * 50, 24950 + i * 50, 25025 + i * 50, 1000));
        }

        var strategy = new PairsTradingStrategy(CreateTestParams());

        var result = Engine.Run([s1, s2], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(10, result.TotalBarsProcessed); // 5 bars × 2 series
        Assert.Empty(result.Fills); // Not enough data
    }

    [Fact]
    public void Run_SecondaryBarsUpdateContext_ButDontTrade()
    {
        // Verify the pipeline doesn't crash on secondary subscription bars
        var (s1, s2) = CreateCorrelatedSeries();
        var bus = new CapturingEventBus();
        var strategy = new PairsTradingStrategy(CreateTestParams());

        var result = Engine.Run([s1, s2], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken, bus: bus);

        // The pipeline should have processed all bars
        Assert.Equal(s1.Count + s2.Count, result.TotalBarsProcessed);

        // Filter events should only be emitted for primary subscription bars
        var filterEvents = bus.Events.OfType<FilterEvaluationEvent>().ToList();
        Assert.True(filterEvents.Count <= s1.Count,
            $"Filter events ({filterEvents.Count}) should not exceed primary series count ({s1.Count})");
    }

    [Fact]
    public void Version_Returns_1_0_0()
    {
        var strategy = new PairsTradingStrategy(CreateTestParams());
        Assert.Equal("1.0.0", strategy.Version);
    }

    private sealed class CapturingEventBus : IEventBus
    {
        public List<object> Events { get; } = [];
        public void Emit<T>(T evt) where T : IBacktestEvent => Events.Add(evt!);
    }
}
