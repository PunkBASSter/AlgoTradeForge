using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.DataPlane;

public class StrategyDispatchTests
{
    private static readonly TimeFrame OneMinute = TimeFrame.Parse("1m");

    private sealed class RecordingStrategy : IInt64BarStrategy, ITradeTickStrategy
    {
        public string Version => "test";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }

        public Int64Bar? LastBarComplete;
        public Int64Bar? LastBarStart;
        public TradeTick? LastTick;
        public DataFeedSubscription? LastBarCompleteSub;
        public DataFeedSubscription? LastBarStartSub;
        public DataFeedSubscription? LastTickSub;

        public void OnBarStart(Int64Bar bar, DataFeedSubscription subscription)
        {
            LastBarStart = bar;
            LastBarStartSub = subscription;
        }

        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription)
        {
            LastBarComplete = bar;
            LastBarCompleteSub = subscription;
        }

        public void OnTradeTick(in TradeTick tick, DataFeedSubscription subscription)
        {
            LastTick = tick;
            LastTickSub = subscription;
        }
    }

    // Bar strategy that does NOT implement ITradeTickStrategy: a TickSubscription on it
    // must route nothing (the capability gate, not a routing flag).
    private sealed class NonTickStrategy : IInt64BarStrategy
    {
        public string Version => "test";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }
    }

    private static (LiveSessionRegistration reg, Channel<Action> ch, RecordingStrategy strat) FakeTimeBarReg(
        string instrument, TimeFrame tf)
    {
        var asset = CryptoAsset.Create(instrument, "Binance", 2);
        var resolved = TestSubs.Of(asset, tf);
        var raw = new TimeBarSubscription(instrument, "Binance", DataFeedRole.Primary, tf);
        var ch = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropNewest, SingleReader = true });
        var strat = new RecordingStrategy();
        var reg = new LiveSessionRegistration(
            Guid.NewGuid(), strat, [resolved], ch.Writer);
        return (reg, ch, strat);
    }

    private static (LiveSessionRegistration reg, Channel<Action> ch, RecordingStrategy strat) FakeTickReg(
        string instrument)
    {
        var asset = CryptoAsset.Create(instrument, "Binance", 2);
        var resolved = TestSubs.Of(asset, default, FeedKey: "tick");
        var raw = new TickSubscription(instrument, "Binance", DataFeedRole.Primary);
        var ch = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropNewest, SingleReader = true });
        var strat = new RecordingStrategy();
        var reg = new LiveSessionRegistration(
            Guid.NewGuid(), strat, [resolved], ch.Writer);
        return (reg, ch, strat);
    }

    // A TickSubscription on a strategy that is NOT ITradeTickStrategy — must route nothing.
    private static (LiveSessionRegistration reg, Channel<Action> ch) FakeNonTickCapableTickReg(string instrument)
    {
        var asset = CryptoAsset.Create(instrument, "Binance", 2);
        var resolved = TestSubs.Of(asset, default, FeedKey: "tick");
        var raw = new TickSubscription(instrument, "Binance", DataFeedRole.Primary);
        var ch = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropNewest, SingleReader = true });
        var reg = new LiveSessionRegistration(
            Guid.NewGuid(), new NonTickStrategy(), [resolved], ch.Writer);
        return (reg, ch);
    }

    [Fact]
    public void Bar_fans_out_to_all_sessions_subscribed_to_that_instrument_and_spec()
    {
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var (regA, chA, stratA) = FakeTimeBarReg("BTCUSDT", OneMinute);
        var (regB, chB, stratB) = FakeTimeBarReg("BTCUSDT", OneMinute);
        dispatch.Register(regA);
        dispatch.Register(regB);

        var bar = new Int64Bar(1, 100, 110, 90, 105, 50);
        dispatch.DispatchBar("BTCUSDT", BarSpecKey.TimeBar(OneMinute), in bar, isStart: false);

        Assert.True(chA.Reader.TryRead(out var aAction));
        aAction!();
        Assert.True(chB.Reader.TryRead(out var bAction));
        bAction!();
        Assert.Equal(105, stratA.LastBarComplete!.Value.Close);
        Assert.Equal(105, stratB.LastBarComplete!.Value.Close);
        Assert.Equal("BTCUSDT", stratA.LastBarCompleteSub!.RequireAsset().Name);
    }

    [Fact]
    public void Completed_bar_always_delivers_to_bar_strategy()
    {
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var (reg, ch, strat) = FakeTimeBarReg("BTCUSDT", OneMinute);
        dispatch.Register(reg);

        var bar = new Int64Bar(1, 100, 110, 90, 105, 50);
        dispatch.DispatchBar("BTCUSDT", BarSpecKey.TimeBar(OneMinute), in bar, isStart: false);

        Assert.True(ch.Reader.TryRead(out var action));
        action!();
        Assert.Equal(105, strat.LastBarComplete!.Value.Close);
    }

    [Fact]
    public void IsStart_routes_to_OnBarStart()
    {
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var (reg, ch, strat) = FakeTimeBarReg("BTCUSDT", OneMinute);
        dispatch.Register(reg);

        var bar = new Int64Bar(2, 200, 210, 190, 205, 60);
        dispatch.DispatchBar("BTCUSDT", BarSpecKey.TimeBar(OneMinute), in bar, isStart: true);

        Assert.True(ch.Reader.TryRead(out var action));
        action!();
        Assert.Equal(205, strat.LastBarStart!.Value.Close);
        Assert.Null(strat.LastBarComplete);
    }

    [Fact]
    public void Bar_does_not_fan_out_to_other_instrument_or_spec()
    {
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var (reg, ch, _) = FakeTimeBarReg("BTCUSDT", OneMinute);
        dispatch.Register(reg);

        var bar = new Int64Bar(1, 100, 110, 90, 105, 50);
        dispatch.DispatchBar("ETHUSDT", BarSpecKey.TimeBar(OneMinute), in bar, isStart: false);
        dispatch.DispatchBar("BTCUSDT", BarSpecKey.TimeBar(TimeFrame.Parse("5m")), in bar, isStart: false);

        Assert.False(ch.Reader.TryRead(out _));
    }

    [Fact]
    public void DispatchTick_routes_to_tick_capable_subscribers_only()
    {
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var (tickReg, tickCh, tickStrat) = FakeTickReg("BTCUSDT");
        var (barReg, barCh, _) = FakeTimeBarReg("BTCUSDT", OneMinute);
        dispatch.Register(tickReg);
        dispatch.Register(barReg);

        var tick = new TradeTick(7, 12345, 5, 99, AggressorSide.Buy);
        dispatch.DispatchTick("BTCUSDT", in tick);

        Assert.True(tickCh.Reader.TryRead(out var action));
        action!();
        Assert.Equal(99, tickStrat.LastTick!.Value.Sequence);
        Assert.Equal("ticks", tickStrat.LastTickSub!.FeedKey());

        // Bar-only subscription (no TickSubscription) gets nothing.
        Assert.False(barCh.Reader.TryRead(out _));
    }

    [Fact]
    public void DispatchTick_does_not_route_to_non_tick_capable_strategy_with_tick_subscription()
    {
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var (reg, ch) = FakeNonTickCapableTickReg("BTCUSDT");
        dispatch.Register(reg);

        var tick = new TradeTick(7, 12345, 5, 99, AggressorSide.Buy);
        dispatch.DispatchTick("BTCUSDT", in tick);

        // Has a TickSubscription but the strategy is NOT ITradeTickStrategy — capability gate blocks it.
        Assert.False(ch.Reader.TryRead(out _));
    }

    [Fact]
    public void DispatchTick_does_not_fan_out_to_other_instrument()
    {
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var (reg, ch, _) = FakeTickReg("BTCUSDT");
        dispatch.Register(reg);

        var tick = new TradeTick(7, 12345, 5, 99, AggressorSide.Buy);
        dispatch.DispatchTick("ETHUSDT", in tick);

        Assert.False(ch.Reader.TryRead(out _));
    }

    [Fact]
    public void Unregister_stops_delivery()
    {
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var (reg, ch, _) = FakeTimeBarReg("BTCUSDT", OneMinute);
        dispatch.Register(reg);
        dispatch.Unregister(reg.SessionId);

        var bar = new Int64Bar(1, 100, 110, 90, 105, 50);
        dispatch.DispatchBar("BTCUSDT", BarSpecKey.TimeBar(OneMinute), in bar, isStart: false);

        Assert.False(ch.Reader.TryRead(out _));
    }

    [Fact]
    public void Captured_bar_value_is_correct_after_closure_runs_and_dispatch_returns()
    {
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var (reg, ch, strat) = FakeTimeBarReg("BTCUSDT", OneMinute);
        dispatch.Register(reg);

        DispatchScopedBar(dispatch);

        Assert.True(ch.Reader.TryRead(out var action));
        action!();
        Assert.Equal(105, strat.LastBarComplete!.Value.Close);
        Assert.Equal(50, strat.LastBarComplete!.Value.Volume);
    }

    private static void DispatchScopedBar(StrategyDispatch dispatch)
    {
        var bar = new Int64Bar(1, 100, 110, 90, 105, 50);
        dispatch.DispatchBar("BTCUSDT", BarSpecKey.TimeBar(OneMinute), in bar, isStart: false);
    }
}
