using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Events;

public class EventBusTests
{
    private sealed class RecordingSink : ISink
    {
        public List<byte[]> Received { get; } = [];

        public void Write(ReadOnlyMemory<byte> utf8Json)
        {
            Received.Add(utf8Json.ToArray());
        }
    }

    private static readonly DateTimeOffset Ts = new(2025, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Backtest_Mode_Passes_Exportable_BarEvent()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.Emit(new BarEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));

        Assert.Single(sink.Received);
    }

    [Fact]
    public void Backtest_Mode_Drops_NonExportable_BarEvent()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.Emit(new BarEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, false));

        Assert.Empty(sink.Received);
    }

    [Fact]
    public void Optimization_Mode_Drops_BarEvent_Passes_OrdFillEvent()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Optimization, [sink]);

        bus.Emit(new BarEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));
        bus.Emit(new OrderFillEvent(Ts, "engine", 1, "BTCUSDT", OrderSide.Buy, 50000, 1.5m, 10));

        Assert.Single(sink.Received);
    }

    [Fact]
    public void Live_Mode_Passes_SigEvent_Drops_BarEvent()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Live, [sink]);

        bus.Emit(new BarEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));
        bus.Emit(new SignalEvent(Ts, "strategy", "MomentumSignal", "BTCUSDT", "Long", 0.85m, "Breakout"));

        Assert.Single(sink.Received);
    }

    [Fact]
    public void NonSubscriptionBound_Events_Ignore_IsExportable_Concern()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        // SignalEvent is not ISubscriptionBoundEvent, so it always passes mode check
        bus.Emit(new SignalEvent(Ts, "strategy", "Signal1", "BTCUSDT", "Long", 1.0m, null));

        Assert.Single(sink.Received);
    }

    [Fact]
    public void FanOut_To_Multiple_Sinks_All_Receive_Identical_Bytes()
    {
        var sink1 = new RecordingSink();
        var sink2 = new RecordingSink();
        var sink3 = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink1, sink2, sink3]);

        bus.Emit(new WarningEvent(Ts, "engine", "Low volume detected"));

        Assert.Single(sink1.Received);
        Assert.Single(sink2.Received);
        Assert.Single(sink3.Received);
        Assert.Equal(sink1.Received[0], sink2.Received[0]);
        Assert.Equal(sink1.Received[0], sink3.Received[0]);
    }

    [Fact]
    public void Sequence_Numbers_Are_Monotonic()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.Emit(new WarningEvent(Ts, "engine", "warn1"));
        bus.Emit(new WarningEvent(Ts, "engine", "warn2"));
        bus.Emit(new WarningEvent(Ts, "engine", "warn3"));

        Assert.Equal(3, sink.Received.Count);

        // Parse sq values from JSON
        var sequences = sink.Received
            .Select(b => System.Text.Json.JsonDocument.Parse(b))
            .Select(doc => doc.RootElement.GetProperty("sq").GetInt64())
            .ToList();

        Assert.Equal([1, 2, 3], sequences);
    }

    [Fact]
    public void Dropped_Events_Do_Not_Consume_Sequence_Numbers()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.Emit(new WarningEvent(Ts, "engine", "warn1"));                                          // sq=1
        bus.Emit(new BarEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, false));   // dropped
        bus.Emit(new WarningEvent(Ts, "engine", "warn2"));                                          // sq=2

        Assert.Equal(2, sink.Received.Count);

        var sequences = sink.Received
            .Select(b => System.Text.Json.JsonDocument.Parse(b))
            .Select(doc => doc.RootElement.GetProperty("sq").GetInt64())
            .ToList();

        Assert.Equal([1, 2], sequences);
    }
}
