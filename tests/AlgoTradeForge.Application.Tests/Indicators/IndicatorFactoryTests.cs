using AlgoTradeForge.Application.Indicators;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Application.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Indicators;

public class IndicatorFactoryTests
{
    private static readonly Asset Aapl = TestAssets.Aapl;
    private static readonly DataSubscription ExportableSub = new(Aapl, TimeSpan.FromMinutes(1), IsExportable: true);
    private static readonly DataSubscription NonExportableSub = new(Aapl, TimeSpan.FromMinutes(1), IsExportable: false);

    [Fact]
    public void PassthroughFactory_ReturnsSameInstance()
    {
        var inner = new DeltaZigZag(0.5m, 100L);

        var result = PassthroughIndicatorFactory.Instance.Create(inner, ExportableSub);

        Assert.Same(inner, result);
    }

    [Fact]
    public void EmittingFactory_ReturnsDecoratedIndicator()
    {
        var bus = new CapturingEventBus();
        var factory = new EmittingIndicatorFactory(bus);
        var inner = new DeltaZigZag(0.5m, 100L);

        var result = factory.Create(inner, ExportableSub);

        Assert.NotSame(inner, result);
        Assert.IsType<EmittingIndicatorDecorator<Int64Bar, long>>(result);
    }

    [Fact]
    public void DecoratedIndicator_DelegatesAllProperties()
    {
        var bus = new CapturingEventBus();
        var inner = new DeltaZigZag(0.5m, 100L);
        var decorated = new EmittingIndicatorDecorator<Int64Bar, long>(inner, bus, ExportableSub);

        Assert.Equal(inner.Name, decorated.Name);
        Assert.Equal(inner.Measure, decorated.Measure);
        Assert.Same(inner.Buffers, decorated.Buffers);
        Assert.Equal(inner.MinimumHistory, decorated.MinimumHistory);
        Assert.Equal(inner.CapacityLimit, decorated.CapacityLimit);
    }

    [Fact]
    public void DecoratedIndicator_EmitsIndEvent_AfterCompute()
    {
        var bus = new CapturingEventBus();
        var inner = new DeltaZigZag(0.5m, 100L);
        var decorated = new EmittingIndicatorDecorator<Int64Bar, long>(inner, bus, ExportableSub);

        var barTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var bars = new List<Int64Bar> { TestBars.Create(1000, 1100, 900, 1050, timestampMs: barTime.ToUnixTimeMilliseconds()) };
        decorated.Compute(bars);

        var evt = Assert.Single(bus.Events);
        var indEvent = Assert.IsType<IndicatorEvent>(evt);
        Assert.Equal("DeltaZigZag", indEvent.IndicatorName);
        Assert.Equal(IndicatorMeasure.Price, indEvent.Measure);
        Assert.Equal("indicator.DeltaZigZag", indEvent.Source);
        Assert.True(indEvent.IsExportable);
        Assert.True(indEvent.Values.ContainsKey("Value"));
        Assert.Equal(1100L, indEvent.Values["Value"]);
        Assert.Equal(barTime, indEvent.Timestamp);
    }

    [Fact]
    public void DecoratedIndicator_EventTimestamp_MatchesBarTimestamp()
    {
        var bus = new CapturingEventBus();
        var inner = new DeltaZigZag(0.5m, 100L);
        var decorated = new EmittingIndicatorDecorator<Int64Bar, long>(inner, bus, ExportableSub);

        var t1 = new DateTimeOffset(2025, 3, 1, 10, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2025, 3, 1, 10, 1, 0, TimeSpan.Zero);

        var bars = new List<Int64Bar> { TestBars.Create(1000, 1100, 900, 1050, timestampMs: t1.ToUnixTimeMilliseconds()) };
        decorated.Compute(bars);

        bars.Add(TestBars.Create(1050, 1200, 1000, 1150, timestampMs: t2.ToUnixTimeMilliseconds()));
        decorated.Compute(bars);

        // 3 events: IndicatorEvent(t1), IndicatorEvent(t2), IndicatorMutationEvent(t1 retroactive clear)
        Assert.Equal(3, bus.Events.Count);
        var evt1 = Assert.IsType<IndicatorEvent>(bus.Events[0]);
        var evt2 = Assert.IsType<IndicatorEvent>(bus.Events[1]);
        var mutEvt = Assert.IsType<IndicatorMutationEvent>(bus.Events[2]);
        Assert.Equal(t1, evt1.Timestamp);
        Assert.Equal(t2, evt2.Timestamp);
        Assert.Equal(t1, mutEvt.Timestamp); // Retroactive clear uses the old bar's timestamp
    }

    [Fact]
    public void DecoratedIndicator_EmitsIndMutEvent_OnMutationCompute()
    {
        var bus = new CapturingEventBus();
        var inner = new DeltaZigZag(0.5m, 100L);
        var decorated = new EmittingIndicatorDecorator<Int64Bar, long>(inner, bus, ExportableSub);

        var barTime = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var barMs = barTime.ToUnixTimeMilliseconds();
        var bars = new List<Int64Bar> { TestBars.Create(1000, 1100, 900, 1050, timestampMs: barMs) };
        decorated.Compute(bars);
        bus.Events.Clear();

        // Same series length → mutation
        bars[0] = TestBars.Create(1000, 1200, 900, 1150, timestampMs: barMs);
        decorated.Compute(bars);

        var evt = Assert.Single(bus.Events);
        var mutEvent = Assert.IsType<IndicatorMutationEvent>(evt);
        Assert.Equal(barTime, mutEvent.Timestamp);
    }

    [Fact]
    public void DecoratedIndicator_NoEvent_IfComputeNotCalled()
    {
        var bus = new CapturingEventBus();
        _ = new EmittingIndicatorDecorator<Int64Bar, long>(new DeltaZigZag(0.5m, 100L), bus, ExportableSub);

        Assert.Empty(bus.Events);
    }

    [Fact]
    public void DecoratedIndicator_UsesSubscriptionExportable()
    {
        var bus = new CapturingEventBus();
        var inner = new DeltaZigZag(0.5m, 100L);
        var decorated = new EmittingIndicatorDecorator<Int64Bar, long>(inner, bus, NonExportableSub);

        var bars = new List<Int64Bar> { TestBars.Create(1000, 1100, 900, 1050) };
        decorated.Compute(bars);

        var evt = Assert.Single(bus.Events);
        var indEvent = Assert.IsType<IndicatorEvent>(evt);
        Assert.False(indEvent.IsExportable);
    }

    [Fact]
    public void DecoratedIndicator_SkipsEvent_WhenAllValuesAreDefault()
    {
        var bus = new CapturingEventBus();
        var inner = new DeltaZigZag(0.5m, 100L);
        var decorated = new EmittingIndicatorDecorator<Int64Bar, long>(inner, bus, ExportableSub);

        // Bar 1: H=1200 → initial pivot, buffer[0]=1200 → event emitted
        var bars = new List<Int64Bar> { TestBars.Create(1000, 1200, 900, 1100) };
        decorated.Compute(bars);
        Assert.Single(bus.Events);

        // Bar 2: H=1150 (no new high), L=1110 (above reversal threshold 1200-100=1100) → buffer[1]=0 → event suppressed
        bars.Add(TestBars.Create(1100, 1150, 1110, 1120));
        decorated.Compute(bars);
        Assert.Single(bus.Events);
    }

    [Fact]
    public void EmitsRetroactiveMutation_OnPivotRelocation()
    {
        var bus = new CapturingEventBus();
        var inner = new DeltaZigZag(0.5m, 100L);
        var decorated = new EmittingIndicatorDecorator<Int64Bar, long>(inner, bus, ExportableSub);

        var t1 = new DateTimeOffset(2025, 6, 15, 12, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2025, 6, 15, 12, 1, 0, TimeSpan.Zero);

        // Bar 1: High=1100, pivot at index 0
        var bars = new List<Int64Bar> { TestBars.Create(1000, 1100, 900, 1050, timestampMs: t1.ToUnixTimeMilliseconds()) };
        decorated.Compute(bars);

        // Bar 2: High=1200 > 1100, pivot relocates to index 1, old pivot at index 0 zeroed
        bars.Add(TestBars.Create(1050, 1200, 1000, 1150, timestampMs: t2.ToUnixTimeMilliseconds()));
        decorated.Compute(bars);

        // Should have: IndicatorEvent(bar1), IndicatorEvent(bar2), IndicatorMutationEvent(retroactive clear of bar1)
        Assert.Equal(3, bus.Events.Count);

        var evt1 = Assert.IsType<IndicatorEvent>(bus.Events[0]);
        Assert.Equal(t1, evt1.Timestamp);

        var evt2 = Assert.IsType<IndicatorEvent>(bus.Events[1]);
        Assert.Equal(t2, evt2.Timestamp);

        // Retroactive mutation for old pivot zeroed at index 0
        var mutEvt = Assert.IsType<IndicatorMutationEvent>(bus.Events[2]);
        Assert.Equal(t1, mutEvt.Timestamp); // Timestamp of the cleared bar, not the current bar
        Assert.True(mutEvt.Values.ContainsKey("Value"));
        Assert.Null(mutEvt.Values["Value"]); // null because SkipZeroValues=true and value=0L
    }

    [Fact]
    public void MultiTimeframe_EachSubscription_EmitsIndependently()
    {
        // Arrange — M1 + H1 subscriptions, each with its own decorated DeltaZigZag
        var bus = new CapturingEventBus();
        var factory = new EmittingIndicatorFactory(bus);

        var m1Sub = new DataSubscription(Aapl, TimeSpan.FromMinutes(1), IsExportable: true);
        var h1Sub = new DataSubscription(Aapl, TimeSpan.FromHours(1), IsExportable: true);

        var strategy = new DualTfStrategy(new DualTfParams { DataSubscriptions = [m1Sub, h1Sub] }, factory);

        var start = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var m1Series = TestBars.CreateSeries(start, TimeSpan.FromMinutes(1), 3, startPrice: 1000);
        var h1Series = TestBars.CreateSeries(start, TimeSpan.FromHours(1), 1, startPrice: 5000);

        var engine = new BacktestEngine(new BarMatcher(), new OrderValidator());
        var options = new BacktestOptions
        {
            InitialCash = 100_000L,
            Asset = Aapl,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
        };

        // Act
        engine.Run([m1Series, h1Series], strategy, options, bus: bus);

        // Assert — 3 ind events from M1 indicator + 1 from H1 indicator = 4 total
        var indEvents = bus.Events.OfType<IndicatorEvent>().ToList();
        Assert.Equal(4, indEvents.Count);

        // First 3 from M1 processing (timestamp order: M1 bars at T+0m, T+1m, T+2m all < H1 at T+0h... but T+0m == T+0h)
        // Actually, M1 bar at T+0 and H1 bar at T+0 share the same timestamp.
        // Engine delivers same-timestamp bars in subscription order → M1 first, H1 second.
        // So at T+0: M1 bar → ind event, H1 bar → ind event (2 events)
        // Then T+1m: M1 bar → ind event
        // Then T+2m: M1 bar → ind event
        // Total: 4 ind events

        // Verify H1 indicator name appears exactly once (H1 has 1 bar)
        var h1Events = indEvents.Where(e => e.IndicatorName == "DeltaZigZag_H1").ToList();
        var m1Events = indEvents.Where(e => e.IndicatorName == "DeltaZigZag_M1").ToList();
        Assert.Equal(3, m1Events.Count);
        Assert.Single(h1Events);
    }

    private sealed class DualTfParams : StrategyParamsBase;

    private sealed class DualTfStrategy(DualTfParams p, IIndicatorFactory? indicators = null) : StrategyBase<DualTfParams>(p, indicators)
    {
        public override string Version => "1.0.0";
        private IIndicator<Int64Bar, long> _m1Dzz = null!;
        private IIndicator<Int64Bar, long> _h1Dzz = null!;
        private readonly List<Int64Bar> _m1History = [];
        private readonly List<Int64Bar> _h1History = [];

        public override void OnInit()
        {
            _m1Dzz = Indicators.Create(new NamedDeltaZigZag("DeltaZigZag_M1", 0.5m, 100L), DataSubscriptions[0]);
            _h1Dzz = Indicators.Create(new NamedDeltaZigZag("DeltaZigZag_H1", 0.5m, 100L), DataSubscriptions[1]);
        }

        public override void OnBarComplete(Int64Bar bar, DataSubscription subscription, IOrderContext orders)
        {
            if (subscription == DataSubscriptions[0])
            {
                _m1History.Add(bar);
                _m1Dzz.Compute(_m1History);
            }
            else
            {
                _h1History.Add(bar);
                _h1Dzz.Compute(_h1History);
            }
        }
    }

    private sealed class NamedDeltaZigZag(string name, decimal delta, long minimumThreshold) : IIndicator<Int64Bar, long>
    {
        private readonly DeltaZigZag _inner = new(delta, minimumThreshold);

        public string Name => name;
        public IndicatorMeasure Measure => _inner.Measure;
        public IReadOnlyDictionary<string, IndicatorBuffer<long>> Buffers => _inner.Buffers;
        public int MinimumHistory => _inner.MinimumHistory;
        public int? CapacityLimit => _inner.CapacityLimit;
        public void Compute(IReadOnlyList<Int64Bar> series) => _inner.Compute(series);
    }
}
