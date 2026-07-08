using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Binance;
using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using AlgoTradeForge.LiveHost.WebApi;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.WebApi.Tests;

public sealed class IbRoundTripTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // TimeSec = Unix seconds; AAPL scale (PriceExp=2, QtyExp=0).
    // Tick 1: price=296.98 → scaled 29698; qty=3 → scaled 3; CSV unscaled: 296.98, 3
    // Tick 2: price=296.99 → scaled 29699; qty=1 → scaled 1; CSV unscaled: 296.99, 1
    private const long Ts1Sec = 1_700_000_000L;
    private static readonly long Ts1Ms = Ts1Sec * 1000L;

    private const string Instrument = "AAPL";

    public IbRoundTripTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"IbRT_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // Minimal IIbMarketDataSession fake: captures the trade sink on SubscribeTrades so
    // the test can push IbTradeUpdates after the pump has subscribed.
    private sealed class FakeIbSession : IIbMarketDataSession
    {
        private readonly TaskCompletionSource<Action<IbTradeUpdate>> _sinkTcs
            = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action? Reconnected { add { } remove { } }

        public Task Connect(CancellationToken ct = default) => Task.CompletedTask;

        public int SubscribeTrades(ResolvedIbContract contract, Action<IbTradeUpdate> sink)
        {
            _sinkTcs.TrySetResult(sink);
            return 1;
        }

        public int SubscribeRealtimeBars(ResolvedIbContract contract, Action<IbRealtimeBar> sink) => 1;

        public void Unsubscribe(int reqId) { }

        public Task<Action<IbTradeUpdate>> WaitForTradeSink(CancellationToken ct) =>
            _sinkTcs.Task.WaitAsync(ct);
    }

    // Tap that cancels the pump CTS once N trades have been dispatched.
    // The tap fires after RelayWriter.WriteTrade, so archival is complete before cancellation.
    private sealed class NthTradeCancelTap(int n, CancellationTokenSource pumpCts) : IRelayTradeTap
    {
        private int _count;

        public void OnTrade(string instrument, in TradeTick tick)
        {
            if (Interlocked.Increment(ref _count) >= n)
                pumpCts.Cancel();
        }
    }

    private (RelayPumpHostedService svc, FakeIbSession session) BuildPumpService(
        CancellationTokenSource pumpCts, int expectedTrades)
    {
        var fakeSession = new FakeIbSession();

        var contractSpec = new IbContract("AAPL", IbSecType.Stk, "SMART", "NASDAQ", "USD");
        var resolvedContract = new ResolvedIbContract(contractSpec, ConId: 265598, LocalSymbol: "AAPL", LastTradeDate: "");

        var contractResolver = Substitute.For<IIbContractResolver>();
        contractResolver
            .Resolve(Arg.Any<IbContract>(), Arg.Any<CancellationToken>())
            .Returns(resolvedContract);

        var aapl = new EquityAsset { Name = "AAPL", Exchange = "NASDAQ" };
        var assetResolver = Substitute.For<IIbInstrumentAssetResolver>();
        assetResolver
            .Resolve(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Asset>(aapl));

        var opts = new IbDataPlaneOptions
        {
            InstrumentScales = { [Instrument] = new TickScale(PriceExp: 2, QtyExp: 0) },
        };

        var connector = new IbVenueConnector(fakeSession, contractResolver, assetResolver, opts);

        var timeProvider = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(Ts1Ms));
        var pumpOpts = Options.Create(new RelayPumpOptions
        {
            LocalRoot = _root,
            KeyPrefix = "live-md",
            HeartbeatInterval = TimeSpan.FromMinutes(60),
            UploadInterval = TimeSpan.FromMinutes(60),
        });

        // NthTradeCancelTap cancels pumpCts after expectedTrades are dispatched.
        // The tap fires after WriteTrade, so all ticks are safely in the relay writer.
        var tap = new NthTradeCancelTap(expectedTrades, pumpCts);

        var svc = new RelayPumpHostedService(
            connector, pumpOpts, _storage, tap, timeProvider,
            NullLogger<RelayPumpHostedService>.Instance,
            Substitute.For<ICollectionConfigStore>());

        return (svc, fakeSession);
    }

    private StreamCanonicalizer<T> BuildCanonicalizer<T>(IStreamProjection<T> projection)
        where T : IFramePayload<T>
    {
        var cursors = new FileStreamCursorStore(_storage);
        return new StreamCanonicalizer<T>(_storage, projection, cursors, "live-md", "_canon-cursors");
    }

    [Fact]
    public async Task IbTicks_RoundTrip_To_CanonicalCsv_Lossless()
    {
        // The pump's IbVenueConnector.Stream drains a BoundedChannel that never completes
        // naturally. NthTradeCancelTap cancels pumpCts after 2 trades are dispatched
        // (after RelayWriter.WriteTrade, so archival is guaranteed before cancellation).
        // RunPumpOnce catches OperationCanceledException and performs a final flush+sweep.
        using var pumpCts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var (svc, fakeSession) = BuildPumpService(pumpCts, expectedTrades: 2);

        var pumpTask = svc.RunPumpOnce([Instrument], pumpCts.Token);

        // Wait until IbVenueConnector has called SubscribeTrades so the sink is ready.
        var sink = await fakeSession.WaitForTradeSink(Ct);

        // Push two ticks into the channel. The pump loop reads them, calls WriteTrade,
        // then the tap cancels pumpCts on the 2nd trade.
        // scale(2,0): price×100, qty×1 → 296.98 → 29698; 296.99 → 29699
        sink(new IbTradeUpdate(Ts1Sec, 296.98, 3m));
        sink(new IbTradeUpdate(Ts1Sec, 296.99, 1m));

        await pumpTask;

        // Canonicalize the archived relay stream into CSV.
        var map = new InstrumentAssetDirMap(_root, new Dictionary<string, string>());
        var writer = new DailyTickCsvWriter(
            _storage, _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<DailyTickCsvWriter>.Instance,
            new WriteLockManager());
        var canon = BuildCanonicalizer<TradeTick>(new TradeProjection(writer, map, NullLogger<TradeProjection>.Instance));

        var framesProcessed = await canon.Run("ib", Instrument, Ct);
        Assert.Equal(2, framesProcessed);

        var assetDir = map.Resolve("ib", Instrument);
        var day = DateTimeOffset.FromUnixTimeMilliseconds(Ts1Ms).UtcDateTime.ToString("yyyy-MM-dd");
        var csvKey = Path.Combine(assetDir, "ticks", $"{day}.csv").Replace('\\', '/');

        var lines = await _storage.ReadAllLines(csvKey, Ct);
        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        // Empty digits map -> fallback digits = PriceScaleExp = 2. Stored as scaled long: magnitude*10^2.
        // AggressorSide.Unknown -> is_buyer_maker 0; price 296.98*100 = 29698, qty 3*100 = 300
        Assert.Equal($"{Ts1Ms},29698,300,0,1", lines[1]);
        // price 296.99*100 = 29699, qty 1*100 = 100
        Assert.Equal($"{Ts1Ms},29699,100,0,2", lines[2]);
    }
}
