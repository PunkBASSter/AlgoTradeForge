using System.Threading.Channels;
using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Subscriptions;
using AlgoTradeForge.Domain.Trading;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.DataPlane;
using AlgoTradeForge.LiveHost.WebApi;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AlgoTradeForge.LiveHost.WebApi.Tests.DataPlane;

// Plan-4 open/closed acceptance: ONE seeded TradeEvent stream is pumped through RelayIngest.Pump
// with a real TickRouterTradeTap so the SAME ticks hit BOTH the lossless archival sink AND the
// real data-plane (TickRouter -> StrategyDispatch -> per-session channel -> strategy). Asserts:
//   1. .atft archival still round-trips (no Plan-3 regression introduced by the tap).
//   2. AltBar path: real TickAggregationBarSource feeds OnBarComplete with expected closes.
//   3. Tick path: every tick reaches OnTradeTick with correct values.
public sealed class DataPlaneEndToEndTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Venue = "FAKE";
    private const string Instrument = "BTCUSDT";

    // Venue scale: price /10^5, qty /10^3 (matches LiveRoundTripTests). The asset's ScaleContext
    // mirrors these (TickSize=10^-5, QuantityScale=10^3) so the EqV threshold freezes correctly.
    private const sbyte PriceExp = 5;
    private const sbyte QtyExp = 3;
    private static readonly CryptoAsset Asset =
        CryptoAsset.Create(Instrument, "Binance", decimalDigits: 5, quantityStepSize: 0.001m);
    private static readonly ScaleContext Scale = new(Asset);

    // EqV threshold = 2 base units. base_asset unit -> scaled = 2 * QuantityScale(10^3) = 2000.
    // Each seeded tick carries qty=1000 (=1.0 base), so every 2 ticks completes one bar.
    private const string AltFeedId = "EqV_1m_2";

    private static readonly long Ts0 = new DateTimeOffset(2023, 11, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    // 6 ticks of qty=1000 -> 3 completed EqV bars. Closes = price of the 2nd tick of each pair.
    private static readonly TradeTick[] Ticks =
    [
        new(Ts0 + 0, 5_000_000_000L, 1_000L, 1L, AggressorSide.Buy),
        new(Ts0 + 1, 5_000_100_000L, 1_000L, 2L, AggressorSide.Sell), // bar 1 close = 5_000_100_000
        new(Ts0 + 2, 5_000_200_000L, 1_000L, 3L, AggressorSide.Buy),
        new(Ts0 + 3, 5_000_300_000L, 1_000L, 4L, AggressorSide.Sell), // bar 2 close = 5_000_300_000
        new(Ts0 + 4, 5_000_400_000L, 1_000L, 5L, AggressorSide.Buy),
        new(Ts0 + 5, 5_000_500_000L, 1_000L, 6L, AggressorSide.Sell), // bar 3 close = 5_000_500_000
    ];

    private static readonly long[] ExpectedCloses = [5_000_100_000L, 5_000_300_000L, 5_000_500_000L];

    public DataPlaneEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"DPE2E_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeVenueConnector(IReadOnlyList<IMarketEvent> events) : IVenueConnector
    {
        public string Venue => "FAKE";
        public MarketDataSessionPolicy SessionPolicy => MarketDataSessionPolicy.Concurrent;
        public (sbyte PriceScaleExp, sbyte QtyScaleExp) InstrumentScale(string instrument) => (PriceExp, QtyExp);

        public async IAsyncEnumerable<IMarketEvent> Stream(
            IReadOnlyList<string> instruments,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var ev in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return ev;
            }
        }
    }

    // Records every callback the dispatch delivers to it. No trading logic.
    private sealed class RecordingStrategy : IInt64BarStrategy, ITradeTickStrategy
    {
        public string Version => "1.0";
        public void OnInit() { }
        public void OnTrade(Fill fill, Order order) { }
        public IList<DataFeedSubscription> DataSubscriptions { get; } = [];

        public List<Int64Bar> CompletedBars { get; } = [];
        public List<TradeTick> Ticks { get; } = [];

        public void OnBarComplete(Int64Bar bar, DataFeedSubscription subscription) => CompletedBars.Add(bar);
        public void OnTradeTick(in TradeTick tick, DataFeedSubscription subscription) => Ticks.Add(tick);
    }

    // A per-session market-data queue + a single-reader drain that invokes queued actions —
    // mirrors the connector's processing loop without a live connection. The dispatch writes
    // Action delegates here; the drain runs them on the strategy.
    private sealed record Session(
        RecordingStrategy Strategy,
        LiveSessionRegistration Registration,
        Channel<Action> Channel,
        Task Drain);

    private static Session BuildSession(DataFeedSubscription raw)
    {
        var strategy = new RecordingStrategy();
        var channel = Channel.CreateBounded<Action>(
            new BoundedChannelOptions(capacity: 256) { SingleReader = true });

        var drain = Task.Run(async () =>
        {
            await foreach (var action in channel.Reader.ReadAllAsync())
                action();
        });

        var resolved = SubscriptionResolver.Resolve(raw, Asset);
        var registration = new LiveSessionRegistration(
            SessionId: Guid.NewGuid(),
            Strategy: strategy,
            Subscriptions: [resolved],
            DataWriter: channel.Writer);

        return new Session(strategy, registration, channel, drain);
    }

    [Fact]
    public async Task TickStream_FansToArchival_AndRealDataPlane_BarAndTickPaths()
    {
        // --- Real data-plane wiring (no fakes for dispatch/router/resolver) ---
        var dispatch = new StrategyDispatch(NullLogger<StrategyDispatch>.Instance);
        var ws = new BinanceWebSocketManager(
            "wss://unused.invalid", TimeSpan.FromSeconds(1), maxReconnectAttempts: 0,
            NullLogger.Instance); // AltBar/Tick paths never touch the WS; constructed only to satisfy the resolver.
        var resolver = new BarSourceResolver(ws);
        var router = new TickRouter(resolver, dispatch, NullLogger<TickRouter>.Instance);

        // Bar-path session: AltBar subscription; bars route unconditionally.
        var barSession = BuildSession(
            new AltBarSubscription(Instrument, "Binance", DataFeedRole.Primary, AltFeedId));

        // Tick-path session: Tick subscription; tick routing is capability-driven (ITradeTickStrategy).
        var tickSession = BuildSession(
            new TickSubscription(Instrument, "Binance", DataFeedRole.Primary));

        dispatch.Register(barSession.Registration);
        dispatch.Register(tickSession.Registration);
        await router.EnsureSources(barSession.Registration, _ => Scale);
        await router.EnsureSources(tickSession.Registration, _ => Scale); // tick session: no bar source created

        // --- Drive ONE tick stream through RelayIngest.Pump (via RunPumpOnce) with the real tap ---
        var events = Ticks.Select(t => (IMarketEvent)new TradeEvent(Instrument, t)).ToArray();
        var connector = new FakeVenueConnector(events);
        var tap = new TickRouterTradeTap(router);
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(Ts0));
        var opts = Options.Create(new RelayPumpOptions
        {
            LocalRoot = _root,
            KeyPrefix = "live-md",
            Instruments = [Instrument],
            HeartbeatInterval = TimeSpan.FromMinutes(60),
            UploadInterval = TimeSpan.FromMinutes(60),
        });
        var pump = new RelayPumpHostedService(
            connector, opts, _storage, tap, time, NullLogger<RelayPumpHostedService>.Instance);

        await pump.RunPumpOnce([Instrument], Ct);

        // Pump done -> close session channels and await the drains so all queued callbacks ran.
        barSession.Channel.Writer.Complete();
        tickSession.Channel.Writer.Complete();
        await Task.WhenAll(barSession.Drain, tickSession.Drain);

        // --- Assertion 1: archival .atft still round-trips losslessly (no tap regression) ---
        var map = new InstrumentAssetDirMap(_root, new Dictionary<string, string>());
        var writer = new DailyTickCsvWriter(
            _storage, _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<DailyTickCsvWriter>.Instance,
            new WriteLockManager());
        var cursors = new FileStreamCursorStore(_storage);
        var canon = new StreamCanonicalizer<TradeTick>(
            _storage, new TradeProjection(writer, map), cursors, "live-md", "_canon-cursors");

        var framesProcessed = await canon.Run(Venue, Instrument, Ct);
        Assert.Equal(Ticks.Length, framesProcessed);

        var assetDir = map.Resolve(Venue, Instrument);
        var day = DateTimeOffset.FromUnixTimeMilliseconds(Ts0).UtcDateTime.ToString("yyyy-MM-dd");
        var csvKey = Path.Combine(assetDir, "ticks", $"{day}.csv").Replace('\\', '/');
        var lines = await _storage.ReadAllLines(csvKey, Ct);

        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        var archivedRows = lines.Length - 1;
        Assert.Equal(Ticks.Length, archivedRows);
        // Spot-check first row: Buy -> is_buyer_maker=0; price=50000, qty=1.
        Assert.Equal($"{Ts0},50000,1,0,1", lines[1]);

        // --- Assertion 2: AltBar path delivered the expected completed bars ---
        Assert.Equal(ExpectedCloses.Length, barSession.Strategy.CompletedBars.Count);
        Assert.Equal(ExpectedCloses, barSession.Strategy.CompletedBars.Select(b => b.Close).ToArray());
        Assert.Empty(barSession.Strategy.Ticks); // bar session is NOT subscribed to ticks

        // --- Assertion 3: Tick path delivered every tick with correct values ---
        Assert.Equal(Ticks.Length, tickSession.Strategy.Ticks.Count);
        Assert.Equal(
            Ticks.Select(t => (t.TimestampMs, t.Price, t.Quantity, t.Sequence)).ToArray(),
            tickSession.Strategy.Ticks.Select(t => (t.TimestampMs, t.Price, t.Quantity, t.Sequence)).ToArray());
        Assert.Empty(tickSession.Strategy.CompletedBars); // tick session is NOT subscribed to bars

        // Non-vacuous guards.
        Assert.True(archivedRows > 0, "expected archived rows > 0");
        Assert.True(barSession.Strategy.CompletedBars.Count > 0, "expected completed bars > 0");
        Assert.True(tickSession.Strategy.Ticks.Count > 0, "expected ticks > 0");
    }
}
