using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Events;

public class ExportModeFilteringTests
{
    private static readonly DateTimeOffset Ts = new(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Src = "test";

    private sealed class MemorySink : ISink
    {
        public List<byte[]> Writes { get; } = [];

        public void Write(ReadOnlyMemory<byte> utf8Json) => Writes.Add(utf8Json.ToArray());
    }

    /// <summary>
    /// Creates one instance of every event type that the engine can emit.
    /// Returns the events grouped by their TypeId.
    /// </summary>
    private static IBacktestEvent[] AllEventTypes() =>
    [
        new BarEvent(Ts, Src, "AAPL", "1m", 100, 110, 90, 105, 1000, IsExportable: true),
        new SignalEvent(Ts, Src, "CrossUp", "AAPL", "Long", 0.9m, null),
        new RiskEvent(Ts, Src, "AAPL", true, "CashCheck", null),
        new IndicatorEvent(Ts, Src, "SMA", IndicatorMeasure.Price,
            new Dictionary<string, object?> { ["value"] = 100.0 }, IsExportable: true),
        new OrderPlaceEvent(Ts, Src, 1, "AAPL", OrderSide.Buy, OrderType.Market, 1m, null, null),
        new OrderFillEvent(Ts, Src, 1, "AAPL", OrderSide.Buy, 100, 1m, 0),
        new OrderCancelEvent(Ts, Src, 1, "AAPL", "Cancelled"),
        new OrderRejectEvent(Ts, Src, 1, "AAPL", "Insufficient cash"),
        new PositionEvent(Ts, Src, "AAPL", 1m, 100, 0),
        new RunStartEvent(Ts, Src, "Strat", "AAPL", 100_000, Ts, Ts.AddDays(1), ExportMode.Backtest),
        new RunEndEvent(Ts, Src, 100, 100_000, 5, TimeSpan.FromSeconds(1)),
        new ErrorEvent(Ts, Src, "oops", null),
        new WarningEvent(Ts, Src, "heads up"),
    ];

    private static void EmitAll(IEventBus bus, IBacktestEvent[] events)
    {
        foreach (var evt in events)
            EmitDynamic(bus, evt);
    }

    /// <summary>
    /// Uses the static interface dispatch by calling Emit with the concrete type.
    /// This is needed because EventBus.Emit reads T.TypeId / T.DefaultExportMode via static abstract.
    /// </summary>
    private static void EmitDynamic(IEventBus bus, IBacktestEvent evt)
    {
        switch (evt)
        {
            case BarEvent e: bus.Emit(e); break;
            case SignalEvent e: bus.Emit(e); break;
            case RiskEvent e: bus.Emit(e); break;
            case IndicatorEvent e: bus.Emit(e); break;
            case OrderPlaceEvent e: bus.Emit(e); break;
            case OrderFillEvent e: bus.Emit(e); break;
            case OrderCancelEvent e: bus.Emit(e); break;
            case OrderRejectEvent e: bus.Emit(e); break;
            case PositionEvent e: bus.Emit(e); break;
            case RunStartEvent e: bus.Emit(e); break;
            case RunEndEvent e: bus.Emit(e); break;
            case ErrorEvent e: bus.Emit(e); break;
            case WarningEvent e: bus.Emit(e); break;
            default: throw new InvalidOperationException($"Unknown event type: {evt.GetType().Name}");
        }
    }

    // ── Backtest mode: all events pass through ───────────────────────────

    [Fact]
    public void BacktestMode_AllEventsReachSink()
    {
        var sink = new MemorySink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);
        var events = AllEventTypes();

        EmitAll(bus, events);

        Assert.Equal(events.Length, sink.Writes.Count);
    }

    // ── Optimization mode: only ord.*, pos, run.*, err, warn ────────────

    [Fact]
    public void OptimizationMode_DropsBar_Sig_Risk_Ind()
    {
        var sink = new MemorySink();
        var bus = new EventBus(ExportMode.Optimization, [sink]);
        var events = AllEventTypes();

        EmitAll(bus, events);

        // Events that should pass: ord.place, ord.fill, ord.cancel, ord.reject, pos, run.start, run.end, err, warn = 9
        Assert.Equal(9, sink.Writes.Count);
    }

    [Theory]
    [InlineData(typeof(BarEvent))]
    [InlineData(typeof(SignalEvent))]
    [InlineData(typeof(RiskEvent))]
    [InlineData(typeof(IndicatorEvent))]
    public void OptimizationMode_Drops_SpecificEventType(Type droppedType)
    {
        var sink = new MemorySink();
        var bus = new EventBus(ExportMode.Optimization, [sink]);
        var events = AllEventTypes();

        EmitAll(bus, events);

        // Parse each written JSON to check _t field — none should match the dropped type's TypeId
        var droppedTypeId = GetTypeId(droppedType);
        foreach (var data in sink.Writes)
        {
            var doc = System.Text.Json.JsonDocument.Parse(data);
            var typeId = doc.RootElement.GetProperty("_t").GetString();
            Assert.NotEqual(droppedTypeId, typeId);
        }
    }

    // ── Live mode: bar, ind dropped; sig and risk pass through ──────────

    [Fact]
    public void LiveMode_DropsBar_Ind_ButKeepsSig_Risk()
    {
        var sink = new MemorySink();
        var bus = new EventBus(ExportMode.Live, [sink]);
        var events = AllEventTypes();

        EmitAll(bus, events);

        // Events that should pass: sig, risk, ord.place, ord.fill, ord.cancel, ord.reject, pos, run.start, run.end, err, warn = 11
        Assert.Equal(11, sink.Writes.Count);
    }

    [Theory]
    [InlineData(typeof(BarEvent))]
    [InlineData(typeof(IndicatorEvent))]
    public void LiveMode_Drops_SpecificEventType(Type droppedType)
    {
        var sink = new MemorySink();
        var bus = new EventBus(ExportMode.Live, [sink]);
        var events = AllEventTypes();

        EmitAll(bus, events);

        var droppedTypeId = GetTypeId(droppedType);
        foreach (var data in sink.Writes)
        {
            var doc = System.Text.Json.JsonDocument.Parse(data);
            var typeId = doc.RootElement.GetProperty("_t").GetString();
            Assert.NotEqual(droppedTypeId, typeId);
        }
    }

    [Fact]
    public void LiveMode_SigEventReachesSink()
    {
        var sink = new MemorySink();
        var bus = new EventBus(ExportMode.Live, [sink]);

        bus.Emit(new SignalEvent(Ts, Src, "CrossUp", "AAPL", "Long", 0.9m, null));

        Assert.Single(sink.Writes);
    }

    [Fact]
    public void LiveMode_RiskEventReachesSink()
    {
        var sink = new MemorySink();
        var bus = new EventBus(ExportMode.Live, [sink]);

        bus.Emit(new RiskEvent(Ts, Src, "AAPL", true, "CashCheck", null));

        Assert.Single(sink.Writes);
    }

    private static string GetTypeId(Type eventType) => eventType.Name switch
    {
        nameof(BarEvent) => "bar",
        nameof(SignalEvent) => "sig",
        nameof(RiskEvent) => "risk",
        nameof(IndicatorEvent) => "ind",
        _ => throw new ArgumentException($"Unknown event type: {eventType.Name}")
    };
}
