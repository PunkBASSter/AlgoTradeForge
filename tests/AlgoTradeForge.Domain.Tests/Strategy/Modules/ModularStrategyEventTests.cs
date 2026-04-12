using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules.Filter;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TradeRegistry;
using AlgoTradeForge.Domain.Strategy.Rsi2MeanReversion;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules;

public sealed class ModularStrategyEventTests
{
    private static readonly BacktestEngine Engine = new(new BarMatcher(), new OrderValidator());

    private static BacktestOptions CreateOptions() => new()
    {
        InitialCash = 10_000_000_000L,
        StartTime = DateTimeOffset.MinValue,
        EndTime = DateTimeOffset.MaxValue,
    };

    private static Rsi2Params CreateParams() => new()
    {
        RsiPeriod = 2, OversoldThreshold = 10, OverboughtThreshold = 90,
        TrendFilterPeriod = 50, AtrPeriod = 14,
        AtrFilter = new AtrVolatilityFilterParams { Period = 14, MinAtr = 0, MaxAtr = 0 },
        SignalThreshold = 30, FilterThreshold = 0, DefaultAtrStopMultiplier = 2.0,
        MoneyManagement = new MoneyManagementParams { RiskPercent = 2.0 },
        TradeRegistry = new TradeRegistryParams { MaxConcurrentGroups = 1 },
        DataSubscriptions = [new DataSubscription(TestAssets.BtcUsdt, TimeSpan.FromMinutes(1))],
    };

    private static TimeSeries<Int64Bar> CreateSignalSeries()
    {
        var bars = new List<Int64Bar>();
        var startMs = new DateTimeOffset(2024, 1, 1, 9, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        // 50 bars uptrend then 3 sharp drops (produces RSI < 10 with SMA > price)
        for (var i = 0; i < 50; i++)
        {
            var price = 50000L + i * 100;
            bars.Add(new Int64Bar(startMs + i * 60_000L, price, price + 50, price - 50, price + 50, 1000));
        }
        for (var i = 0; i < 3; i++)
        {
            var price = 54950L - (i + 1) * 500;
            bars.Add(new Int64Bar(startMs + (50 + i) * 60_000L, price + 200, price + 300, price - 100, price, 2000));
        }
        for (var i = 0; i < 5; i++)
        {
            var price = 53450L + (i + 1) * 300;
            bars.Add(new Int64Bar(startMs + (53 + i) * 60_000L, price, price + 100, price - 50, price + 50, 1000));
        }

        var series = new TimeSeries<Int64Bar>();
        foreach (var bar in bars) series.Add(bar);
        return series;
    }

    [Fact]
    public void Run_EmitsFilterEvaluationEvents()
    {
        var bus = new CapturingEventBus();
        var bars = CreateSignalSeries();
        var strategy = new Rsi2MeanReversionStrategy(CreateParams());

        Engine.Run([bars], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken, bus: bus);

        var filterEvents = bus.Events.OfType<FilterEvaluationEvent>().ToList();
        Assert.True(filterEvents.Count > 0,
            "Pipeline should emit FilterEvaluationEvent on each Phase 3 execution");
    }

    [Fact]
    public void Run_EmitsSignalEventsOnEntry()
    {
        var bus = new CapturingEventBus();
        var bars = CreateSignalSeries();
        var strategy = new Rsi2MeanReversionStrategy(CreateParams());

        var result = Engine.Run([bars], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken, bus: bus);

        var signalEvents = bus.Events.OfType<SignalEvent>().ToList();
        if (result.Fills.Count > 0)
        {
            Assert.True(signalEvents.Count > 0,
                "SignalEvent should be emitted when entries occur");
        }
    }

    [Fact]
    public void Run_EmitsExitEvaluationEventsWhenNotFlat()
    {
        var bus = new CapturingEventBus();
        var bars = CreateSignalSeries();
        var strategy = new Rsi2MeanReversionStrategy(CreateParams());

        var result = Engine.Run([bars], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken, bus: bus);

        var exitEvents = bus.Events.OfType<ExitEvaluationEvent>().ToList();

        // If there were any fills, exit evaluation should have run on subsequent bars
        if (result.Fills.Count > 0)
        {
            Assert.True(exitEvents.Count > 0,
                "ExitEvaluationEvent should be emitted during Phase 2 when position active");
        }
    }

    [Fact]
    public void Run_DebugProbe_OnBarProcessedCalled()
    {
        var bars = CreateSignalSeries();
        var strategy = new Rsi2MeanReversionStrategy(CreateParams());
        var probe = new CountingDebugProbe();

        Engine.Run([bars], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken, probe: probe);

        Assert.Equal(bars.Count, probe.BarCount);
        Assert.True(probe.StartCalled);
        Assert.True(probe.EndCalled);
    }

    [Fact]
    public void Run_DebugProbe_SnapshotHasCorrectSequenceNumbers()
    {
        var bars = CreateSignalSeries();
        var strategy = new Rsi2MeanReversionStrategy(CreateParams());
        var probe = new CapturingDebugProbe();

        Engine.Run([bars], strategy, CreateOptions(),
            ct: TestContext.Current.CancellationToken, probe: probe);

        // Verify snapshots have increasing sequence numbers
        for (var i = 1; i < probe.Snapshots.Count; i++)
        {
            Assert.True(probe.Snapshots[i].SequenceNumber > probe.Snapshots[i - 1].SequenceNumber,
                $"Sequence numbers should increase: [{i - 1}]={probe.Snapshots[i - 1].SequenceNumber}, [{i}]={probe.Snapshots[i].SequenceNumber}");
        }
    }

    private sealed class CapturingEventBus : IEventBus
    {
        public List<object> Events { get; } = [];
        public void Emit<T>(T evt) where T : IBacktestEvent => Events.Add(evt!);
    }

    private sealed class CountingDebugProbe : IDebugProbe
    {
        public bool IsActive => true;
        public bool StartCalled { get; private set; }
        public bool EndCalled { get; private set; }
        public int BarCount { get; private set; }

        public void OnRunStart() => StartCalled = true;
        public void OnBarProcessed(DebugSnapshot snapshot) => BarCount++;
        public void OnRunEnd() => EndCalled = true;
    }

    private sealed class CapturingDebugProbe : IDebugProbe
    {
        public bool IsActive => true;
        public List<DebugSnapshot> Snapshots { get; } = [];

        public void OnRunStart() { }
        public void OnBarProcessed(DebugSnapshot snapshot) => Snapshots.Add(snapshot);
        public void OnRunEnd() { }
    }
}
