using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class CanonicalizerDispatchTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public CanonicalizerDispatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CanonDispatch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task ThreeStreams_AllCanonicalize()
    {
        var map = new InstrumentAssetDirMap(_root, new Dictionary<string, string>());
        var cursors = new FileStreamCursorStore(_storage);
        var opts = Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 });

        var tradeWriter = new DailyTickCsvWriter(_storage, _tail, opts, NullLogger<DailyTickCsvWriter>.Instance, new WriteLockManager());
        var quoteWriter = new DailyBookTickerCsvWriter(_storage, _tail, opts, NullLogger<DailyBookTickerCsvWriter>.Instance, new WriteLockManager());
        var sessionWriter = new DailySessionCsvWriter(_storage, _tail, opts, NullLogger<DailySessionCsvWriter>.Instance, new WriteLockManager());

        IStreamCanonicalizer[] canon =
        [
            new StreamCanonicalizer<TradeTick>(_storage, new TradeProjection(tradeWriter, map, NullLogger<TradeProjection>.Instance), cursors, "live-md", "_canon-cursors"),
            new StreamCanonicalizer<QuoteTick>(_storage, new QuoteProjection(quoteWriter, map), cursors, "live-md", "_canon-cursors"),
            new StreamCanonicalizer<SessionEvent>(_storage, new SessionProjection(sessionWriter, map), cursors, "live-md", "_canon-cursors"),
        ];

        await WriteSegment("BTCUSDT", "trades", new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
        await WriteSegment("BTCUSDT", "quotes", new QuoteTick(Ts, 5000000, 100, 5000100, 200, 1));
        await WriteSegment("binance", "_session", new SessionEvent(Ts, SessionEventKind.SessionStart));

        var byStream = canon.ToDictionary(c => c.StreamName, StringComparer.Ordinal);
        foreach (var (inst, stream) in new[] { ("BTCUSDT", "trades"), ("BTCUSDT", "quotes"), ("binance", "_session") })
            await byStream[stream].Run("binance", inst, Ct);

        Assert.True(await _storage.Exists(Path.Combine(map.Resolve("binance", "BTCUSDT"), "ticks",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv"), Ct));
        Assert.True(await _storage.Exists(Path.Combine(map.Resolve("binance", "BTCUSDT"), "book-ticker",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv"), Ct));
        Assert.True(await _storage.Exists(Path.Combine(map.VenueDir("binance"), "_session",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv"), Ct));
    }

    private async Task WriteSegment<T>(string instrumentOrVenue, string stream, T frame) where T : IFramePayload<T>
    {
        using var ms = new MemoryStream();
        using (var w = new SegmentWriter<T>(ms,
            new SegmentHeader(2, 3, 0, Ts, 0, (ushort)T.PayloadSize), leaveOpen: true))
            w.Write(frame);
        await _storage.WriteAllBytes($"live-md/binance/{instrumentOrVenue}/{stream}/{Ts:D13}-{0:D19}.atft", ms.ToArray(), Ct);
    }
}
