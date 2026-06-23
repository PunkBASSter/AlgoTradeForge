using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.DataPlane;

public class TickAggregationBarSourceTests
{
    private static ScaleContext Scale() => new(tickSize: 0.01m);

    [Fact]
    public void EqV_source_emits_a_bar_when_volume_threshold_is_crossed()
    {
        var emitted = new List<Int64Bar>();
        var src = new TickAggregationBarSource("EqV", frozenThreshold: 10, Scale(), (bar, _) => emitted.Add(bar));

        // two ticks of qty 5 => 10 total, threshold 10 => one closed bar after the second tick.
        src.Feed(new TradeTick(1, 100, 5, 1, AggressorSide.Buy));
        src.Feed(new TradeTick(2, 101, 5, 2, AggressorSide.Buy));

        Assert.Single(emitted);
        Assert.Equal(1, emitted[0].TimestampMs);
        Assert.Equal(100, emitted[0].Open);
        Assert.Equal(101, emitted[0].High);
        Assert.Equal(100, emitted[0].Low);
        Assert.Equal(101, emitted[0].Close);
        Assert.Equal(10, emitted[0].Volume);
        Assert.Contains(emitted[0], src.Recent);
    }

    [Fact]
    public void Sub_threshold_ticks_emit_nothing()
    {
        var emitted = new List<Int64Bar>();
        var src = new TickAggregationBarSource("EqV", frozenThreshold: 10, Scale(), (bar, _) => emitted.Add(bar));

        src.Feed(new TradeTick(1, 100, 3, 1, AggressorSide.Buy));

        Assert.Empty(emitted);
        Assert.Empty(src.Recent);
    }

    [Fact]
    public void Multiple_threshold_crossings_emit_multiple_bars()
    {
        var emitted = new List<Int64Bar>();
        var src = new TickAggregationBarSource("EqV", frozenThreshold: 10, Scale(), (bar, _) => emitted.Add(bar));

        src.Feed(new TradeTick(1, 100, 10, 1, AggressorSide.Buy)); // bar 1 closes
        src.Feed(new TradeTick(2, 200, 10, 2, AggressorSide.Sell)); // bar 2 closes

        Assert.Equal(2, emitted.Count);
        Assert.Equal(100, emitted[0].Close);
        Assert.Equal(200, emitted[1].Close);
        Assert.Equal(emitted, src.Recent);
    }

    [Fact]
    public async Task Concurrent_Feed_and_Recent_reads_never_tear()
    {
        const int capacity = 64;
        const int iterations = 5000;
        var src = new TickAggregationBarSource("EqV", frozenThreshold: 10, Scale(), (_, _) => { }, recentCapacity: capacity);

        var ct = TestContext.Current.CancellationToken;
        var feeder = Task.Run(() =>
        {
            for (long i = 1; i <= iterations; i++)
                src.Feed(new TradeTick(i, 100 + i, 10, i, AggressorSide.Buy)); // each tick closes one bar (threshold 10)
        }, ct);

        var reader = Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var snapshot = src.Recent; // must not throw / tear concurrent with Feed
                Assert.True(snapshot.Count <= capacity);
                foreach (var bar in snapshot)
                    Assert.True(bar.Volume == 10); // a torn read would surface as a default/garbage bar
            }
        }, ct);

        await Task.WhenAll(feeder, reader);

        Assert.Equal(capacity, src.Recent.Count);
    }

    [Fact]
    public void Recent_evicts_oldest_at_capacity()
    {
        var emitted = new List<Int64Bar>();
        var src = new TickAggregationBarSource("EqV", frozenThreshold: 10, Scale(), (bar, _) => emitted.Add(bar), recentCapacity: 2);

        for (long i = 1; i <= 3; i++)
            src.Feed(new TradeTick(i, 100 + i, 10, i, AggressorSide.Buy));

        Assert.Equal(3, emitted.Count);
        Assert.Equal(2, src.Recent.Count);
        Assert.Equal(emitted[1], src.Recent[0]);
        Assert.Equal(emitted[2], src.Recent[1]);
    }
}
