using System.Threading.Channels;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.WebApi.Tests.DataPlane;

// Proves the shared-source single-flight property of the catch-up data plane:
//   1. Two sessions subscribing to the SAME (instrument, AltBar feedId) resolve to ONE shared
//      TickAggregationBarSource (TickRouter RefCount semantic).
//   2. The catch-up replay runs EXACTLY ONCE (single-flight): CountingReplaySource.Replays == 1
//      even though two sessions called EnsureSources.
//   3. Both sessions' strategies receive IDENTICAL completed bars from the shared accumulator.
public sealed class CatchupDataPlaneTests
{
    private const string Instrument = "BTCUSDT";
    private const string AltFeedId = "EqV_1m_2"; // EqV threshold=2 base units; qty scale=10^3 -> 2000

    // CryptoAsset (spot) matches the DataPlaneEndToEndTests convention: decimalDigits=5, qty step=0.001.
    private static readonly CryptoAsset Asset =
        CryptoAsset.Create(Instrument, "Binance", decimalDigits: 5, quantityStepSize: 0.001m);
    private static readonly ScaleContext Scale = new(Asset);

    private static readonly long Ts0 =
        new DateTimeOffset(2024, 3, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // 6 ticks of qty=1000 (=1.0 base unit). EqV threshold=2*1000=2000 -> one bar per 2 ticks.
    // 3 completed bars. Closes = price of the 2nd tick of each pair.
    private static readonly TradeTick[] Ticks =
    [
        new(Ts0 + 0, 6_000_000_000L, 1_000L, 1L, AggressorSide.Buy),
        new(Ts0 + 1, 6_000_100_000L, 1_000L, 2L, AggressorSide.Sell), // bar 1 close
        new(Ts0 + 2, 6_000_200_000L, 1_000L, 3L, AggressorSide.Buy),
        new(Ts0 + 3, 6_000_300_000L, 1_000L, 4L, AggressorSide.Sell), // bar 2 close
        new(Ts0 + 4, 6_000_400_000L, 1_000L, 5L, AggressorSide.Buy),
        new(Ts0 + 5, 6_000_500_000L, 1_000L, 6L, AggressorSide.Sell), // bar 3 close
    ];

    private static readonly long[] ExpectedCloses =
        [6_000_100_000L, 6_000_300_000L, 6_000_500_000L];

    // Counts the number of times IReplaySource.Replay() is invoked, delegating to an empty source.
    // Thread-safe via Interlocked (Replay may be called from Start() which runs outside the lifecycle lock).
    private sealed class CountingReplaySource : IReplaySource
    {
        private int _replays;
        public int Replays => Volatile.Read(ref _replays);

        public async IAsyncEnumerable<TradeTick> Replay(
            ReplayRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Interlocked.Increment(ref _replays);
            await Task.CompletedTask;
            yield break; // empty — no historical ticks to replay
        }
    }

    private sealed class RecordingStrategy : IInt64BarStrategy
    {
        public string Version => "1.0";
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];
        public List<Int64Bar> CompletedBars { get; } = [];
        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) => CompletedBars.Add(bar);
    }

    private sealed record Session(
        RecordingStrategy Strategy,
        LiveSessionRegistration Registration,
        Channel<Action> Channel,
        Task Drain);

    private static Session BuildSession()
    {
        var strategy = new RecordingStrategy();
        var channel = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(capacity: 256) { SingleReader = true });
        var drain = Task.Run(async () =>
        {
            await foreach (var action in channel.Reader.ReadAllAsync())
                action();
        });
        // Both sessions subscribe to the same (Instrument, AltFeedId) pair.
        var raw = new AltBarSubscription(Instrument, "Binance", DataFeedRole.Primary, AltFeedId);
        var resolved = SubscriptionResolver.Resolve(raw, Asset);
        var registration = new LiveSessionRegistration(
            SessionId: Guid.NewGuid(),
            Strategy: strategy,
            Subscriptions: [resolved],
            DataWriter: channel.Writer);
        return new Session(strategy, registration, channel, drain);
    }

    [Fact]
    public async Task SharedAltBarSource_CatchupRunsOnce_AndBothSessionsReceiveIdenticalBars()
    {
        var ct = TestContext.Current.CancellationToken;
        var countingReplay = new CountingReplaySource();

        // Build the real BarSourceResolver with the counting replay source.
        // BackfillBudget=0 means no backfill attempts; warmup loader returns empty series.
        var backfill = Substitute.For<IBackfillRequester>();
        var warmupLoader = Substitute.For<IInt64BarLoader>();
        warmupLoader.Load(Arg.Any<DataFeedDescriptor>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TimeSeries<Int64Bar>(1))); // empty but valid (capacity >= 1)

        var ws = new BinanceWebSocketManager(
            "wss://unused.invalid", TimeSpan.FromSeconds(1), maxReconnectAttempts: 0,
            NullLogger.Instance);
        var catchupOptions = new CatchupOptions
        {
            RelayKeyPrefix = "live-md",
            DataRoot = Path.GetTempPath(),
            BackfillBudget = TimeSpan.Zero, // no backfill; declare immediately on any gap
        };
        var resolver = new BarSourceResolver(ws, countingReplay, backfill, warmupLoader, catchupOptions);
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);

        // Two sessions, same (Instrument, AltFeedId) subscription.
        var session1 = BuildSession();
        var session2 = BuildSession();

        dispatch.Register(session1.Registration);
        dispatch.Register(session2.Registration);

        // EnsureSources for session1 → creates the shared source, calls Start() → Replay() called once.
        await router.EnsureSources(session1.Registration, _ => Scale);
        // EnsureSources for session2 → shared source already exists; RefCount++; Start() NOT called again.
        await router.EnsureSources(session2.Registration, _ => Scale);

        // Single-flight assertion: catch-up replay ran exactly once across two sessions.
        Assert.Equal(1, countingReplay.Replays);

        // Publish 6 ticks → the shared EqV source accumulates them → dispatches 3 completed bars
        // to BOTH registered sessions via StrategyDispatch.
        foreach (var tick in Ticks)
            router.Publish(Instrument, in tick);

        // Close both channels and drain pending callbacks so all OnBarComplete calls complete.
        session1.Channel.Writer.Complete();
        session2.Channel.Writer.Complete();
        await Task.WhenAll(session1.Drain, session2.Drain).WaitAsync(ct);

        // Both sessions must have received the same 3 bars from the ONE shared accumulator.
        Assert.Equal(ExpectedCloses.Length, session1.Strategy.CompletedBars.Count);
        Assert.Equal(ExpectedCloses.Length, session2.Strategy.CompletedBars.Count);

        // Identical bars: same closes (EqV accumulator emits deterministically from shared source).
        Assert.Equal(
            ExpectedCloses,
            session1.Strategy.CompletedBars.Select(b => b.Close).ToArray());
        Assert.Equal(
            session1.Strategy.CompletedBars.Select(b => (b.TimestampMs, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray(),
            session2.Strategy.CompletedBars.Select(b => (b.TimestampMs, b.Open, b.High, b.Low, b.Close, b.Volume)).ToArray());

        // Non-vacuous guards.
        Assert.True(session1.Strategy.CompletedBars.Count > 0, "session1 must have received completed bars");
        Assert.True(session2.Strategy.CompletedBars.Count > 0, "session2 must have received completed bars");
    }
}
