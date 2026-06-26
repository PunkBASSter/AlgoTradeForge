using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;
using Xunit;
using static AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery.ReplayAbstractionsTests;

namespace AlgoTradeForge.LiveHost.Application.Tests.DataPlane;

public class TickAggregationBarSourceCatchupTests
{
    private static ScaleContext Scale() => new(tickSize: 0.01m);

    // EqV threshold 40. Warmup feed already produced a completed bar at ts=1000 (open ts).
    // Replay covers source records from boundary=1000 forward; first re-derived bar (ts<=1000) is
    // suppressed (already known), the partial continues into live ticks.
    private static TradeTick Tick(long seq, long ts, long price, long qty) =>
        new(ts, price, qty, seq, AggressorSide.Buy);

    // Feed arriving while _phase == Cold (before Start()) must be buffered, not dropped or fed direct.
    [Fact]
    public async Task Feed_before_Start_is_buffered_and_processed_after_replay_not_dropped()
    {
        // Same warmup bar as the cold-start test.
        var warmupBar = new Int64Bar(1000, 5_000_000, 5_000_100, 4_999_900, 5_000_050, 40);
        var loader = new SingleBarLoader(warmupBar);

        // Replay re-derives the known bar only; no new partial from replay.
        var replayTicks = new[]
        {
            Tick(10, 1000, 5_000_000, 25),   // part of known bar
            Tick(11, 1000, 5_000_050, 15),   // crosses 40 -> re-derives known bar -> SUPPRESSED
        };
        var coord = new CatchupCoordinator(new FakeReplaySource(replayTicks), new FakeBackfillRequester(false), RecoveryPolicy.NoBackfill);

        var dispatched = new List<Int64Bar>();
        var plan = new CatchupPlan(
            coord,
            new ReplayRequest(Btc(), "binance", "ticks", FromTs: 0),
            loader,
            new DataFeedDescriptor("root", "binance", "BTCUSDT_perp", "EqV_40", DataFeedKind.AltBar),
            WarmupBarCount: 256);

        var src = new TickAggregationBarSource("EqV", frozenThreshold: 40, Scale(),
            onBar: (b, _) => dispatched.Add(b), catchup: plan);

        // Feed a tick BEFORE Start() — source is still Cold. seq=12 is contiguous after replay tail (seq=11).
        // 40 units total would complete a new bar, but this tick alone carries only 30 units, so no bar yet.
        src.Feed(Tick(12, 1001, 5_000_060, 30));

        // Pre-condition: no bar dispatched yet (tick is buffered, not fed direct).
        Assert.Empty(dispatched);

        // Now start — replay runs, buffer drains (the early-fed tick is processed), phase goes Live.
        await src.Start(TestContext.Current.CancellationToken);

        // The buffered tick was admitted: warmup bar is in Recent, no exception thrown.
        Assert.Contains(warmupBar, src.Recent);

        // Feed a final 10-unit live tick (seq=13) that — combined with the drained 30-unit buffer tick —
        // crosses the 40-unit threshold and completes a new bar.
        src.Feed(Tick(13, 1002, 5_000_060, 10));
        var bar = Assert.Single(dispatched);
        Assert.True(bar.TimestampMs > 1000, "completed bar must open after the suppressed known bar");
    }

    [Fact]
    public async Task Cold_start_seeds_recent_suppresses_known_bar_and_continues_partial()
    {
        // Persisted alt-bar feed has one completed bar opening at ts=1000.
        var warmupBar = new Int64Bar(1000, 5_000_000, 5_000_100, 4_999_900, 5_000_050, 40);
        var loader = new SingleBarLoader(warmupBar);

        // Replay re-derives the known bar (40 units crossing at seq 11) then 30 units of a NEW partial.
        var replayTicks = new[]
        {
            Tick(10, 1000, 5_000_000, 25),   // part of known bar
            Tick(11, 1000, 5_000_050, 15),   // crosses 40 -> re-derives known bar (open ts 1000) -> SUPPRESSED
            Tick(12, 1001, 5_000_050, 30),   // opens NEW partial (not yet 40)
        };
        var coord = new CatchupCoordinator(new FakeReplaySource(replayTicks), new FakeBackfillRequester(false), RecoveryPolicy.NoBackfill);

        var dispatched = new List<Int64Bar>();
        var plan = new CatchupPlan(
            coord,
            new ReplayRequest(Btc(), "binance", "ticks", FromTs: 0), // FromTs set by Start() from boundary
            loader,
            new DataFeedDescriptor("root", "binance", "BTCUSDT_perp", "EqV_40", DataFeedKind.AltBar),
            WarmupBarCount: 256);

        var src = new TickAggregationBarSource("EqV", frozenThreshold: 40, Scale(),
            onBar: (b, _) => dispatched.Add(b), catchup: plan);

        await src.Start(TestContext.Current.CancellationToken);

        // Recent seeded with the warmup bar; no NEW completed bar dispatched yet (partial = 30 < 40).
        Assert.Contains(warmupBar, src.Recent);
        Assert.Empty(dispatched);

        // 15 more live units (seq 13) crosses 40 -> the partial (30) + 15 completes a NEW bar.
        src.Feed(Tick(13, 1002, 5_000_060, 15));
        var bar = Assert.Single(dispatched);
        Assert.True(bar.TimestampMs > 1000, "new bar must open after the suppressed known bar");
    }
}

file sealed class SingleBarLoader(Int64Bar bar) : IInt64BarLoader
{
    public Task<TimeSeries<Int64Bar>> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to, System.Threading.CancellationToken ct = default)
    {
        var s = new TimeSeries<Int64Bar>();
        s.Add(bar);
        return Task.FromResult(s);
    }
    public Task<DateTimeOffset?> GetLastTimestamp(DataFeedDescriptor feed, System.Threading.CancellationToken ct = default) =>
        Task.FromResult<DateTimeOffset?>(DateTimeOffset.FromUnixTimeMilliseconds(bar.TimestampMs));
}
