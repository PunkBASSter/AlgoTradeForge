using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Collection;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class StreamCanonicalizerTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private readonly InstrumentAssetDirMap _map;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public StreamCanonicalizerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"StreamCanonTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
        _map = new InstrumentAssetDirMap(_root, new CollectionPlanHolder());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private StreamCanonicalizer<TradeTick> NewCanonicalizer()
    {
        var writer = new DailyTickCsvWriter(
            _storage, _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<DailyTickCsvWriter>.Instance, new WriteLockManager());
        var proj = new TradeProjection(writer, _map, NullLogger<TradeProjection>.Instance);
        var cursors = new FileStreamCursorStore(_storage);
        return new StreamCanonicalizer<TradeTick>(_storage, proj, cursors, "live-md", "_canon-cursors");
    }

    // Writes one synthetic .atft trades segment with the given trades; returns its storage key.
    private async Task<string> WriteSegment(long createdAtMs, long firstSeq, params TradeTick[] trades)
    {
        using var ms = new MemoryStream();
        using (var w = new SegmentWriter<TradeTick>(ms,
            new SegmentHeader(PriceScaleExp: 2, QtyScaleExp: 3, EpochBaseMs: 0,
                CreatedAtMs: createdAtMs, FirstSequence: firstSeq, PayloadSize: (ushort)TradeTick.PayloadSize),
            leaveOpen: true))
        {
            foreach (var t in trades) w.Write(t);
        }
        var key = $"live-md/binance/BTCUSDT/trades/{createdAtMs:D13}-{firstSeq:D19}.atft";
        await _storage.WriteAllBytes(key, ms.ToArray(), Ct);
        return key;
    }

    private async Task<string[]> CanonLines()
    {
        var key = Path.Combine(_map.Resolve("binance", "BTCUSDT"), "ticks",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv");
        return await _storage.Exists(key, Ct) ? await _storage.ReadAllLines(key, Ct) : [];
    }

    [Fact]
    public async Task Run_TwoSegments_CanonicalizesAllTradesScaledLong()
    {
        await WriteSegment(Ts, 1, new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
        await WriteSegment(Ts + 1, 2, new TradeTick(Ts + 5, 5000100, 200, 2, AggressorSide.Sell));

        var n = await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);

        Assert.Equal(2, n);
        var lines = await CanonLines();
        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        // empty digits map -> fallback digits = PriceScaleExp = 2 : price *100, qty *100
        Assert.Equal($"{Ts},5000050,12,0,1", lines[1]);
        Assert.Equal($"{Ts + 5},5000100,20,1,2", lines[2]);
    }

    [Fact]
    public async Task Run_Rerun_NoNewRows_Idempotent()
    {
        await WriteSegment(Ts, 1, new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
        await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);
        var afterFirst = await CanonLines();

        // A brand-new canonicalizer (cursor already persisted) over the same segment.
        var n2 = await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);
        var afterSecond = await CanonLines();

        Assert.Equal(0, n2);                       // cursor skips the consumed segment
        Assert.Equal(afterFirst.Length, afterSecond.Length);
    }

    [Fact]
    public async Task Run_ReprocessWithoutCursor_WatermarkDedups()
    {
        // Simulate the crash window: rows flushed, cursor never advanced. Delete the cursor and
        // re-run; the writer's agg_id watermark must drop the already-written rows.
        await WriteSegment(Ts, 1, new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
        await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);

        await _storage.Delete("_canon-cursors/binance/BTCUSDT/trades.cursor", Ct);
        await NewCanonicalizer().Run("binance", "BTCUSDT", Ct);

        var lines = await CanonLines();
        Assert.Equal(2, lines.Length); // header + exactly one row (no duplicate)
    }
}
