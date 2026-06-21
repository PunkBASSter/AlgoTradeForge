using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class EndToEndRoundTripTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public EndToEndRoundTripTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"E2E_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task RelayWrite_Canonicalize_ProducesBacktestReadableTickCsv()
    {
        var map = new InstrumentAssetDirMap(_root, new Dictionary<string, string>());
        var writer = new DailyTickCsvWriter(_storage, _tail,
            Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
            NullLogger<DailyTickCsvWriter>.Instance, new WriteLockManager());
        var canon = new StreamCanonicalizer<TradeTick>(
            _storage, new TradeProjection(writer, map), new FileStreamCursorStore(_storage), "live-md", "_canon-cursors");

        // Relay-side write of two scaled-long trades (price exp 2, qty exp 3).
        using (var ms = new MemoryStream())
        {
            using (var w = new SegmentWriter<TradeTick>(ms,
                new SegmentHeader(2, 3, 0, Ts, 1, (ushort)TradeTick.PayloadSize), leaveOpen: true))
            {
                w.Write(new TradeTick(Ts, 5000050, 123, 1, AggressorSide.Buy));
                w.Write(new TradeTick(Ts + 1000, 5000100, 250, 2, AggressorSide.Sell));
            }
            await _storage.WriteAllBytes($"live-md/binance/BTCUSDT/trades/{Ts:D13}-{1:D19}.atft", ms.ToArray(), Ct);
        }

        await canon.Run("binance", "BTCUSDT", Ct);

        var key = Path.Combine(map.Resolve("binance", "BTCUSDT"), "ticks",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv");
        var lines = await _storage.ReadAllLines(key, Ct);

        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        Assert.Equal($"{Ts},50000.5,0.123,0,1", lines[1]);
        Assert.Equal($"{Ts + 1000},50001,0.25,1,2", lines[2]);
    }
}
