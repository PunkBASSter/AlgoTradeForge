using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery;
using Xunit;
using static AlgoTradeForge.LiveHost.Application.Tests.Live.Recovery.ReplayAbstractionsTests;

namespace AlgoTradeForge.LiveHost.Application.Tests.DataPlane;

public class TickAggregationBarSourceReconnectTests
{
    private static ScaleContext Scale() => new(tickSize: 0.01m);
    private static TradeTick T(long seq, long ts, long price, long qty) => new(ts, price, qty, seq, AggressorSide.Buy);

    [Fact]
    public async Task Gap_in_live_stream_triggers_recovery_then_resumes_live()
    {
        // Arrange:
        //   - Startup replay: empty (no warmup bars, watermark unseeded).
        //   - Live ticks seq 10..13 (4 x qty=10 = 40 units) -> first bar at ts=1000.
        //   - Seq jump 10->13->40 = Gap -> triggers recovery.
        //   - Recovery bridge: seq 14..39 (26 ticks). After bridge, T(40) in buffer is seq 39+1 = Accept.
        var startupTicks = Array.Empty<TradeTick>();
        var bridge = Enumerable.Range(14, 26) // seq 14..39
            .Select(seq => T(seq, 1000 + seq, 5_000_000, 5))
            .ToArray();

        var coord = new CatchupCoordinator(
            new TwoPhaseReplaySource(startupTicks, bridge),
            new FakeBackfillRequester(false),
            RecoveryPolicy.NoBackfill);

        // Collapsed path: CatchupPlan with empty loader + WarmupBarCount=0.
        var plan = new CatchupPlan(
            coord,
            new ReplayRequest(Btc(), "binance", "ticks", 0),
            new EmptyBarLoader(),
            new DataFeedDescriptor("root", "binance", "BTCUSDT_perp", "EqV_40", DataFeedKind.AltBar),
            WarmupBarCount: 0);

        var dispatched = new List<Int64Bar>();
        var src = new TickAggregationBarSource("EqV", frozenThreshold: 40, Scale(),
            onBar: (b, _) => dispatched.Add(b), catchup: plan);

        await src.Start(TestContext.Current.CancellationToken);

        Assert.True(src.IsLive, "should be live after empty-warmup Start");

        // Feed contiguous ticks to advance the watermark (seq 10..13, 4 x qty=10 = 40 units -> first bar).
        src.Feed(T(10, 1000, 5_000_000, 10));
        src.Feed(T(11, 1001, 5_000_000, 10));
        src.Feed(T(12, 1002, 5_000_000, 10));
        src.Feed(T(13, 1003, 5_000_000, 10)); // crosses 40 -> bar emitted, _lastEmittedOpenTs = 1000

        Assert.Single(dispatched); // one bar closed

        // Feed the gap tick: seq 40 (skips 14..39) — triggers single-flight recovery.
        src.Feed(T(40, 5000, 5_000_000, 10));

        await src.WaitForRecoveryIdle(TestContext.Current.CancellationToken);

        // After recovery the source is Live again and the watermark advanced past the bridge.
        Assert.True(src.IsLive, "source must be live after recovery");
    }

    [Fact]
    public async Task No_catchup_plan_gap_is_dropped_not_recovered()
    {
        // When _catchup is null (pure live source), a Gap admission is simply dropped — no recovery.
        var dispatched = new List<Int64Bar>();
        var src = new TickAggregationBarSource("EqV", frozenThreshold: 40, Scale(),
            onBar: (b, _) => dispatched.Add(b));

        // Advance watermark seq 10, then jump to seq 20 (Gap).
        src.Feed(T(10, 1000, 5_000_000, 10));
        src.Feed(T(20, 2000, 5_000_000, 10)); // Gap, no catchup -> dropped

        Assert.True(src.IsLive, "no-catchup source stays live");
        // No recovery task runs; WaitForRecoveryIdle returns immediately.
        await src.WaitForRecoveryIdle(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Second_gap_while_recovering_does_not_start_a_second_recovery()
    {
        // Recovery is single-flight: a second Gap while CatchingUp is buffered (not a new recovery).
        // After the first recovery completes, the buffered second-gap tick is drained (gap -> dropped).
        //
        // Expected Replay call count = 2:
        //   call 0: Start()'s cold-start StreamFromBoundary (phase0 = empty startupTicks)
        //   call 1: RunRecovery()'s reconnect StreamFromBoundary (phase1 = bridge)
        // A broken latch (second recovery launched) would produce call count >= 3.
        var startupTicks = Array.Empty<TradeTick>();
        var bridge = new[] { T(11, 1001, 5_000_000, 10), T(12, 1002, 5_000_000, 10) };

        var replaySource = new TwoPhaseReplaySource(startupTicks, bridge);
        var coord = new CatchupCoordinator(
            replaySource,
            new FakeBackfillRequester(false),
            RecoveryPolicy.NoBackfill);

        var plan = new CatchupPlan(
            coord,
            new ReplayRequest(Btc(), "binance", "ticks", 0),
            new EmptyBarLoader(),
            new DataFeedDescriptor("root", "binance", "BTCUSDT_perp", "EqV_40", DataFeedKind.AltBar),
            WarmupBarCount: 0);

        var dispatched = new List<Int64Bar>();
        var src = new TickAggregationBarSource("EqV", frozenThreshold: 40, Scale(),
            onBar: (b, _) => dispatched.Add(b), catchup: plan);

        await src.Start(TestContext.Current.CancellationToken);

        src.Feed(T(10, 1000, 5_000_000, 10)); // seed watermark at seq=10

        // First gap (seq jump 10->20) triggers recovery.
        src.Feed(T(20, 2000, 5_000_000, 10));

        // Second gap during recovery (seq 20->50) — should be buffered, not launch a second recovery.
        src.Feed(T(50, 5000, 5_000_000, 10));

        await src.WaitForRecoveryIdle(TestContext.Current.CancellationToken);

        Assert.True(src.IsLive, "source must be live after single recovery completes");
        // 2 = 1 cold-start Replay (Start) + 1 reconnect Replay (RunRecovery). A double-recovery = 3+.
        Assert.Equal(2, replaySource.CallCount);
    }
}

// Yields ticks from phase[0] on first Replay call, phase[1] on second, empty thereafter.
file sealed class TwoPhaseReplaySource(
    IReadOnlyList<TradeTick> phase0,
    IReadOnlyList<TradeTick> phase1) : IReplaySource
{
    private int _callCount;

    // Total number of times Replay has been invoked — used to assert single-flight.
    internal int CallCount => Volatile.Read(ref _callCount);

    public async IAsyncEnumerable<TradeTick> Replay(ReplayRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var idx = System.Threading.Interlocked.Increment(ref _callCount) - 1;
        var ticks = idx switch { 0 => phase0, 1 => phase1, _ => [] };
        foreach (var t in ticks) { ct.ThrowIfCancellationRequested(); yield return t; await Task.Yield(); }
    }
}

file sealed class EmptyBarLoader : IInt64BarLoader
{
    public Task<TimeSeries<Int64Bar>> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to, CancellationToken ct = default)
        => Task.FromResult(new TimeSeries<Int64Bar>());

    public Task<DateTimeOffset?> GetLastTimestamp(DataFeedDescriptor feed, CancellationToken ct = default)
        => Task.FromResult<DateTimeOffset?>(null);
}
