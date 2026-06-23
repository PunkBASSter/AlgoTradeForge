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

public class TickRouterTests
{
    private const string EqVFeedId = "EqV_1m_500";
    private static readonly BarSpecKey EqVSpec = BarSpecKey.AltBar(EqVFeedId);

    private sealed record BarCall(string Instrument, BarSpecKey Spec, Int64Bar Bar, bool IsStart);

    private sealed class RecordingDispatch : IStrategyDispatch
    {
        public readonly List<BarCall> Bars = [];
        public readonly List<(string Instrument, TradeTick Tick)> Ticks = [];

        public void Register(LiveSessionRegistration registration) { }
        public void Unregister(Guid sessionId) { }
        public void DispatchBar(string instrument, BarSpecKey spec, in Int64Bar bar, bool isStart) =>
            Bars.Add(new BarCall(instrument, spec, bar, isStart));
        public void DispatchTick(string instrument, in TradeTick tick) =>
            Ticks.Add((instrument, tick));
    }

    // Resolver returning a real TickAggregationBarSource for alt-bar subs, null for tick subs.
    private sealed class FakeResolver(string typeCode, long threshold) : IBarSourceResolver
    {
        public int ResolveCalls;

        public IBarSource? Resolve(string instrument, DataFeedSubscription subscription, ScaleContext scale, Action<Int64Bar, bool> onBar)
        {
            ResolveCalls++;
            if (subscription is TickSubscription) return null;
            return new TickAggregationBarSource(typeCode, threshold, scale, onBar);
        }
    }

    // Tick-fed source that records how many times Start() was invoked.
    private sealed class StartCountingSource : ITickDrivenBarSource
    {
        public int StartCalls;
        public IReadOnlyList<Int64Bar> Recent => [];
        public Task Start() { StartCalls++; return Task.CompletedTask; }
        public void Feed(in TradeTick tick) { }
    }

    // Returns the SAME source instance for every Resolve so two sessions share it (one creation).
    private sealed class SharedSourceResolver(StartCountingSource source) : IBarSourceResolver
    {
        public int ResolveCalls;

        public IBarSource? Resolve(string instrument, DataFeedSubscription subscription, ScaleContext scale, Action<Int64Bar, bool> onBar)
        {
            ResolveCalls++;
            return subscription is TickSubscription ? null : source;
        }
    }

    private sealed class NoopStrategy : IInt64BarStrategy
    {
        public string Version => "test";
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public void OnBarStart(Int64Bar bar, DataFeedSubscription subscription) { }
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) { }
    }

    private static LiveSessionRegistration AltBarReg(string instrument, string feedId)
    {
        var asset = CryptoAsset.Create(instrument, "Binance", 2);
        var resolved = TestSubs.Of(asset, default, FeedKey: feedId);
        var raw = new AltBarSubscription(instrument, "Binance", DataFeedRole.Primary, feedId);
        var ch = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropNewest, SingleReader = true });
        return new LiveSessionRegistration(
            Guid.NewGuid(), new NoopStrategy(), [resolved], [raw], ch.Writer);
    }

    private static ScaleContext ScaleFor(string instrument) => new(0.01m);

    private static TradeTick Tick(long ts, long price, long qty) =>
        new(ts, price, qty, ts, AggressorSide.Buy);

    [Fact]
    public async Task Publish_feeds_tick_fed_source_and_dispatches_bar_on_close()
    {
        var dispatch = new RecordingDispatch();
        var resolver = new FakeResolver("EqV", threshold: 30);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);

        await router.EnsureSources(AltBarReg("BTCUSDT", EqVFeedId), ScaleFor);

        router.Publish("BTCUSDT", Tick(1, 100, 10));
        router.Publish("BTCUSDT", Tick(2, 110, 10));
        router.Publish("BTCUSDT", Tick(3, 105, 10)); // cumulative volume 30 >= 30 -> close bar

        var barCall = Assert.Single(dispatch.Bars);
        Assert.Equal("BTCUSDT", barCall.Instrument);
        Assert.Equal(EqVSpec, barCall.Spec);
        Assert.False(barCall.IsStart);
        Assert.Equal(105, barCall.Bar.Close);
        Assert.Equal(100, barCall.Bar.Open);
        Assert.Equal(110, barCall.Bar.High);
        Assert.Equal(30, barCall.Bar.Volume);

        Assert.Equal(3, dispatch.Ticks.Count);
        Assert.All(dispatch.Ticks, t => Assert.Equal("BTCUSDT", t.Instrument));
    }

    [Fact]
    public async Task Second_session_same_instrument_spec_shares_source()
    {
        var dispatch = new RecordingDispatch();
        var resolver = new FakeResolver("EqV", threshold: 30);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);

        await router.EnsureSources(AltBarReg("BTCUSDT", EqVFeedId), ScaleFor);
        await router.EnsureSources(AltBarReg("BTCUSDT", EqVFeedId), ScaleFor);

        Assert.Equal(1, resolver.ResolveCalls);

        router.Publish("BTCUSDT", Tick(1, 100, 10));
        router.Publish("BTCUSDT", Tick(2, 110, 10));
        router.Publish("BTCUSDT", Tick(3, 105, 10));

        Assert.Single(dispatch.Bars); // single shared source emits once
    }

    [Fact]
    public async Task RemoveSources_keeps_shared_source_alive_until_last_sharer_leaves()
    {
        var dispatch = new RecordingDispatch();
        var resolver = new FakeResolver("EqV", threshold: 30);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);

        var regA = AltBarReg("BTCUSDT", EqVFeedId);
        var regB = AltBarReg("BTCUSDT", EqVFeedId);
        await router.EnsureSources(regA, ScaleFor);
        await router.EnsureSources(regB, ScaleFor);

        await router.RemoveSources(regA.SessionId); // refCount 2 -> 1, source survives

        router.Publish("BTCUSDT", Tick(1, 100, 10));
        router.Publish("BTCUSDT", Tick(2, 110, 10));
        router.Publish("BTCUSDT", Tick(3, 105, 10));
        Assert.Single(dispatch.Bars);

        await router.RemoveSources(regB.SessionId); // refCount 1 -> 0, source dropped
        dispatch.Bars.Clear();

        router.Publish("BTCUSDT", Tick(4, 100, 10));
        router.Publish("BTCUSDT", Tick(5, 110, 10));
        router.Publish("BTCUSDT", Tick(6, 105, 10));
        Assert.Empty(dispatch.Bars); // no source left -> nothing fed
    }

    [Fact]
    public async Task EnsureSources_starts_source_once_on_creation_and_not_on_reuse()
    {
        var dispatch = new RecordingDispatch();
        var source = new StartCountingSource();
        var resolver = new SharedSourceResolver(source);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);

        await router.EnsureSources(AltBarReg("BTCUSDT", EqVFeedId), ScaleFor);
        Assert.Equal(1, source.StartCalls); // started exactly once on creation
        Assert.Equal(1, resolver.ResolveCalls);

        await router.EnsureSources(AltBarReg("BTCUSDT", EqVFeedId), ScaleFor); // reuse: refCount++ only
        Assert.Equal(1, source.StartCalls); // NOT started again on the shared-source reuse path
        Assert.Equal(1, resolver.ResolveCalls);
    }

    [Fact]
    public async Task RecentBars_returns_sources_emitted_bars_after_publish()
    {
        var dispatch = new RecordingDispatch();
        var resolver = new FakeResolver("EqV", threshold: 30);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);
        await router.EnsureSources(AltBarReg("BTCUSDT", EqVFeedId), ScaleFor);

        router.Publish("BTCUSDT", Tick(1, 100, 10));
        router.Publish("BTCUSDT", Tick(2, 110, 10));
        router.Publish("BTCUSDT", Tick(3, 105, 10)); // closes one bar

        var recent = router.RecentBars("BTCUSDT", EqVSpec);
        var bar = Assert.Single(recent);
        Assert.Equal(100, bar.Open);
        Assert.Equal(105, bar.Close);
    }

    [Fact]
    public void RecentBars_returns_empty_for_unknown_source()
    {
        var dispatch = new RecordingDispatch();
        var resolver = new FakeResolver("EqV", threshold: 30);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);

        Assert.Empty(router.RecentBars("BTCUSDT", EqVSpec));
    }

    [Fact]
    public void Publish_for_instrument_without_sources_still_dispatches_tick()
    {
        var dispatch = new RecordingDispatch();
        var resolver = new FakeResolver("EqV", threshold: 30);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);

        router.Publish("ETHUSDT", Tick(1, 100, 10));

        Assert.Empty(dispatch.Bars);
        var t = Assert.Single(dispatch.Ticks);
        Assert.Equal("ETHUSDT", t.Instrument);
    }

    [Fact]
    public async Task Concurrent_publish_and_lifecycle_does_not_throw()
    {
        var dispatch = new RecordingDispatch();
        var resolver = new FakeResolver("EqV", threshold: 1_000_000);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);
        await router.EnsureSources(AltBarReg("BTCUSDT", EqVFeedId), ScaleFor);

        var stop = false;
        var publisher = Task.Run(() =>
        {
            long ts = 0;
            while (!Volatile.Read(ref stop))
                router.Publish("BTCUSDT", Tick(++ts, 100, 1));
        }, TestContext.Current.CancellationToken);

        for (var i = 0; i < 200; i++)
        {
            var reg = AltBarReg("ETHUSDT", EqVFeedId);
            await router.EnsureSources(reg, ScaleFor);
            await router.RemoveSources(reg.SessionId);
        }

        Volatile.Write(ref stop, true);
        await publisher;
    }
}
