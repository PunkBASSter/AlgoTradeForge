using AlgoTradeForge.Domain.History;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Collection;
using AlgoTradeForge.LiveHost.WebApi;
using AlgoTradeForge.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.LiveHost.WebApi.Tests;

public sealed class LiveRoundTripTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Known timestamps within one UTC day (2023-11-15).
    private static readonly long Ts1 = new DateTimeOffset(2023, 11, 15, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly long Ts2 = Ts1 + 1;

    // Venue name from the fake connector below.
    private const string Venue = "FAKE";
    private const string Instrument = "BTCUSDT";

    // Scale exponents: 5 for price, 3 for quantity (same as RelayPumpHostedServiceTests).
    // Unscale: price / 10^5, qty / 10^3.
    private const sbyte PriceExp = 5;
    private const sbyte QtyExp = 3;

    public LiveRoundTripTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"LiveRT_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        // Storage DataRoot = _root; both producer (upload target) and consumer share this instance.
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class NoopTap : IRelayTradeTap
    {
        public void OnTrade(string instrument, in TradeTick tick) { }
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

    private RelayPumpHostedService BuildPumpService(IReadOnlyList<IMarketEvent> events)
    {
        var connector = new FakeVenueConnector(events);
        var timeProvider = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(Ts1));
        var opts = Options.Create(new RelayPumpOptions
        {
            LocalRoot = _root,
            KeyPrefix = "live-md",
            HeartbeatInterval = TimeSpan.FromMinutes(60),
            UploadInterval = TimeSpan.FromMinutes(60),
        });
        return new RelayPumpHostedService(
            connector, opts, _storage, new NoopTap(), timeProvider,
            NullLogger<RelayPumpHostedService>.Instance,
            Substitute.For<ICollectionConfigStore>());
    }

    private StreamCanonicalizer<T> BuildCanonicalizer<T>(IStreamProjection<T> projection)
        where T : IFramePayload<T>
    {
        var cursors = new FileStreamCursorStore(_storage);
        return new StreamCanonicalizer<T>(_storage, projection, cursors, "live-md", "_canon-cursors");
    }

    [Fact]
    public async Task LiveTicks_RoundTrip_To_CanonicalCsv_Lossless()
    {
        // Source trades: scaled longs — price / 10^5, qty / 10^3.
        // TradeTick(ts, price, qty, sequence, aggressor)
        var trades = new IMarketEvent[]
        {
            new TradeEvent(Instrument, new TradeTick(Ts1, 5_000_000_000L, 1_000L, 1L, AggressorSide.Sell)),
            new TradeEvent(Instrument, new TradeTick(Ts2, 5_000_100_000L, 2_000L, 2L, AggressorSide.Buy)),
        };

        var svc = BuildPumpService(trades);
        await svc.RunPumpOnce([Instrument], Ct);

        var map = new InstrumentAssetDirMap(_root, new CollectionPlanHolder());
        var writer = new DailyTickCsvWriter(
            _storage, _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<DailyTickCsvWriter>.Instance,
            new WriteLockManager());
        var canon = BuildCanonicalizer<TradeTick>(new TradeProjection(writer, map, NullLogger<TradeProjection>.Instance));

        var framesProcessed = await canon.Run(Venue, Instrument, Ct);
        Assert.Equal(2, framesProcessed);

        var assetDir = map.Resolve(Venue, Instrument);
        var day = DateTimeOffset.FromUnixTimeMilliseconds(Ts1).UtcDateTime.ToString("yyyy-MM-dd");
        var csvKey = Path.Combine(assetDir, "ticks", $"{day}.csv").Replace('\\', '/');

        var lines = await _storage.ReadAllLines(csvKey, Ct);
        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        // Empty digits map -> fallback digits = PriceScaleExp = 5. Stored as scaled long: magnitude*10^5.
        // Sell -> is_buyer_maker = 1; price 50000*1e5 = 5000000000, qty 1*1e5 = 100000
        Assert.Equal($"{Ts1},5000000000,100000,1,1", lines[1]);
        // Buy -> is_buyer_maker = 0; price 50001*1e5 = 5000100000, qty 2*1e5 = 200000
        Assert.Equal($"{Ts2},5000100000,200000,0,2", lines[2]);
    }

    [Fact]
    public async Task LiveQuotes_RoundTrip_To_CanonicalCsv_OpenClosed()
    {
        // Open/closed re-assertion: the SAME relay→canonicalizer machinery handles QuoteTick
        // without any edits to canonicalizer production code.
        var quotes = new IMarketEvent[]
        {
            new QuoteEvent(Instrument, new QuoteTick(Ts1, 5_000_000_000L, 10_000L, 5_000_100_000L, 5_000L, 1L)),
        };

        var svc = BuildPumpService(quotes);
        await svc.RunPumpOnce([Instrument], Ct);

        var map = new InstrumentAssetDirMap(_root, new CollectionPlanHolder());
        var bookWriter = new DailyBookTickerCsvWriter(
            _storage, _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<DailyBookTickerCsvWriter>.Instance,
            new WriteLockManager());
        var canon = BuildCanonicalizer<QuoteTick>(new QuoteProjection(bookWriter, map));

        var framesProcessed = await canon.Run(Venue, Instrument, Ct);
        Assert.Equal(1, framesProcessed);

        var assetDir = map.Resolve(Venue, Instrument);
        var day = DateTimeOffset.FromUnixTimeMilliseconds(Ts1).UtcDateTime.ToString("yyyy-MM-dd");
        var csvKey = Path.Combine(assetDir, "book-ticker", $"{day}.csv").Replace('\\', '/');

        var lines = await _storage.ReadAllLines(csvKey, Ct);
        Assert.Equal("ts,bid_price,bid_qty,ask_price,ask_qty,update_id", lines[0]);
        // bid_price = 50000000000 / 10^5 = 50000, bid_qty = 10000 / 10^3 = 10,
        // ask_price = 50001000000 / 10^5 = 50001, ask_qty = 5000 / 10^3 = 5, update_id = 1
        Assert.Equal($"{Ts1},50000,10,50001,5,1", lines[1]);
    }
}
