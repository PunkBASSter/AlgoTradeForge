using AlgoTradeForge.Application.Events;
using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Events;
using AlgoTradeForge.Domain.Trading;
using NSubstitute;
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

    [Fact]
    public void Emit_Calls_Probe_OnEventEmitted_With_Correct_TypeId_And_Sequence()
    {
        var sink = new RecordingSink();
        var probe = Substitute.For<IDebugProbe>();
        var bus = new EventBus(ExportMode.Backtest, [sink], probe);

        bus.Emit(new WarningEvent(Ts, "engine", "test warning"));
        bus.Emit(new SignalEvent(Ts, "strategy", "Sig1", "BTCUSDT", "Long", 1.0m, null));

        probe.Received(1).OnEventEmitted("warn", 1);
        probe.Received(1).OnEventEmitted("sig", 2);
    }

    [Fact]
    public void Filtered_Events_Do_Not_Call_Probe()
    {
        var sink = new RecordingSink();
        var probe = Substitute.For<IDebugProbe>();
        var bus = new EventBus(ExportMode.Backtest, [sink], probe);

        // Non-exportable bar — filtered out
        bus.Emit(new BarEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, false));

        probe.DidNotReceive().OnEventEmitted(Arg.Any<string>(), Arg.Any<long>());
    }

    [Fact]
    public void Mutations_Filtered_By_Default()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.Emit(new BarMutationEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));

        Assert.Empty(sink.Received);
    }

    [Fact]
    public void SetMutationsEnabled_Allows_Mutation_Events()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        bus.SetMutationsEnabled(true);
        bus.Emit(new BarMutationEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));

        Assert.Single(sink.Received);
    }

    [Fact]
    public void MutationsEnabled_Toggle_Takes_Effect_Immediately()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        // Mutations off by default — dropped
        bus.Emit(new BarMutationEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));
        Assert.Empty(sink.Received);

        // Toggle on — passes
        bus.SetMutationsEnabled(true);
        bus.Emit(new BarMutationEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));
        Assert.Single(sink.Received);

        // Toggle off again — dropped
        bus.SetMutationsEnabled(false);
        bus.Emit(new BarMutationEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));
        Assert.Single(sink.Received); // still 1
    }

    [Fact]
    public void NonMutation_Events_Not_Affected_By_Mutation_Filter()
    {
        var sink = new RecordingSink();
        var bus = new EventBus(ExportMode.Backtest, [sink]);

        // Mutations off — non-mutation events still pass
        bus.Emit(new BarEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));
        bus.Emit(new WarningEvent(Ts, "engine", "test"));

        Assert.Equal(2, sink.Received.Count);
    }

    [Fact]
    public void Mutation_Events_Do_Not_Call_Probe_When_Filtered()
    {
        var sink = new RecordingSink();
        var probe = Substitute.For<IDebugProbe>();
        var bus = new EventBus(ExportMode.Backtest, [sink], probe);

        bus.Emit(new BarMutationEvent(Ts, "engine", "BTCUSDT", "1m", 100, 110, 90, 105, 1000, true));

        probe.DidNotReceive().OnEventEmitted(Arg.Any<string>(), Arg.Any<long>());
    }
}
