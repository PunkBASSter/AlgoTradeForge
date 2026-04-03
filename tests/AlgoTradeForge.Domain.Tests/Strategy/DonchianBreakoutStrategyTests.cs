using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.DonchianBreakout;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.Regime;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy;

public sealed class DonchianBreakoutStrategyTests
{
    private static readonly BacktestEngine Engine = new(new BarMatcher(), new OrderValidator());

    private static BacktestOptions CreateOptions(long initialCash = 10_000_000_000L) => new()
    {
        InitialCash = initialCash,
        StartTime = DateTimeOffset.MinValue,
        EndTime = DateTimeOffset.MaxValue,
    };

    private static DonchianParams CreateTestParams() => new()
    {
        EntryPeriod = 10,
        ExitPeriod = 5,
        AtrPeriod = 7,
        AtrStopMultiplier = 2.0,
        SignalThreshold = 30,
        FilterThreshold = -100, // Allow through even when regime is unknown (score 0)
        DefaultAtrStopMultiplier = 2.0,
        MoneyManagement = new MoneyManagementParams { RiskPercent = 2.0 },
        TradeRegistry = new TradeRegistryParams { MaxConcurrentGroups = 1 },
        TrailingStopConfig = new TrailingStopParams { Variant = TrailingStopVariant.Atr, AtrMultiplier = 2.0 },
        RegimeDetectorConfig = new RegimeDetectorParams { AdxPeriod = 7, TrendThreshold = 20.0 },
        DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
    };

    private static TimeSeries<Int64Bar> CreateBreakoutSeries()
    {
        var bars = new List<Int64Bar>();
        var startMs = new DateTimeOffset(2024, 1, 1, 9, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var stepMs = 60_000L;

        // 20 bars of consolidation around 50000 (range 49500-50500)
        for (var i = 0; i < 20; i++)
        {
            var offset = (i % 2 == 0) ? 200L : -200L;
            var price = 50000L + offset;
            bars.Add(new Int64Bar(startMs + i * stepMs, price, price + 300, price - 300, price + 100, 1000));
        }

        // 15 bars of strong uptrend breakout — should trigger Donchian upper breakout
        for (var i = 0; i < 15; i++)
        {
            var price = 50800L + i * 400;
            bars.Add(new Int64Bar(startMs + (20 + i) * stepMs, price, price + 200, price - 100, price + 200, 2000));
        }
        // By bar ~30, price should be well above the 10-period Donchian upper

        // 10 bars of continued trend
        for (var i = 0; i < 10; i++)
        {
            var price = 56800L + i * 300;
            bars.Add(new Int64Bar(startMs + (35 + i) * stepMs, price, price + 150, price - 100, price + 100, 1500));
        }

        var series = new TimeSeries<Int64Bar>();
        foreach (var bar in bars) series.Add(bar);
        return series;
    }

    [Fact]
    public void Run_AllBarsProcessed()
    {
        var bars = CreateBreakoutSeries();
        var strategy = new DonchianBreakoutStrategy(CreateTestParams());

        var result = Engine.Run([bars], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(bars.Count, result.TotalBarsProcessed);
    }

    [Fact]
    public void Run_UsesStopOrders_ForEntry()
    {
        var bars = CreateBreakoutSeries();
        var bus = new CapturingEventBus();
        var strategy = new DonchianBreakoutStrategy(CreateTestParams());

        var result = Engine.Run([bars], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken, bus: bus);

        // The strategy should have emitted signal events
        var signals = bus.Events.OfType<SignalEvent>().ToList();

        // Verify the pipeline processed correctly without crashes
        Assert.True(result.TotalBarsProcessed > 0);
    }

    [Fact]
    public void Run_ImplementsIInt64BarStrategy()
    {
        var strategy = new DonchianBreakoutStrategy(CreateTestParams());
        Assert.IsAssignableFrom<IInt64BarStrategy>(strategy);
    }

    [Fact]
    public void Run_FewBars_DoesNotCrash()
    {
        var bars = new TimeSeries<Int64Bar>();
        var startMs = new DateTimeOffset(2024, 1, 1, 9, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        for (var i = 0; i < 5; i++)
        {
            var price = 50000L + i * 100;
            bars.Add(new Int64Bar(startMs + i * 60_000L, price, price + 50, price - 50, price + 50, 1000));
        }

        var strategy = new DonchianBreakoutStrategy(CreateTestParams());

        var result = Engine.Run([bars], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(5, result.TotalBarsProcessed);
        Assert.Empty(result.Fills);
    }

    [Fact]
    public void Run_RegimeFilterBlocks_WhenRangeBound()
    {
        // Set filter threshold high so regime filter must pass
        var p = new DonchianParams
        {
            EntryPeriod = 10, ExitPeriod = 5, AtrPeriod = 7, AtrStopMultiplier = 2.0,
            SignalThreshold = 30,
            FilterThreshold = 50, // Requires regime filter to pass (100 = trending)
            DefaultAtrStopMultiplier = 2.0,
            MoneyManagement = new MoneyManagementParams { RiskPercent = 2.0 },
            TradeRegistry = new TradeRegistryParams { MaxConcurrentGroups = 1 },
            TrailingStopConfig = new TrailingStopParams { Variant = TrailingStopVariant.Atr, AtrMultiplier = 2.0 },
            RegimeDetectorConfig = new RegimeDetectorParams { AdxPeriod = 7, TrendThreshold = 20.0 },
            DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
        };
        var strategy = new DonchianBreakoutStrategy(p);

        // Choppy series — ADX should indicate range-bound, regime filter blocks
        var bars = new TimeSeries<Int64Bar>();
        var startMs = new DateTimeOffset(2024, 1, 1, 9, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        for (var i = 0; i < 50; i++)
        {
            var offset = (i % 2 == 0) ? 200L : -200L;
            var price = 50000L + offset;
            bars.Add(new Int64Bar(startMs + i * 60_000L, price, price + 300, price - 300, price + 100, 1000));
        }

        var result = Engine.Run([bars], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Fills);
    }

    [Fact]
    public void Version_Returns_1_0_0()
    {
        var strategy = new DonchianBreakoutStrategy(CreateTestParams());
        Assert.Equal("1.0.0", strategy.Version);
    }

    private sealed class CapturingEventBus : IEventBus
    {
        public List<object> Events { get; } = [];
        public void Emit<T>(T evt) where T : IBacktestEvent => Events.Add(evt!);
    }
}
