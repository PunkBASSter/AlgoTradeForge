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

public sealed class ProjectionTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private readonly LocalTailIndex _tail;
    private readonly InstrumentAssetDirMap _map;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly long Ts =
        new DateTimeOffset(2024, 3, 15, 12, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

    public ProjectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ProjectionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
        _tail = new LocalTailIndex(_storage);
        _map = new InstrumentAssetDirMap(_root, new Dictionary<string, string>());
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static SegmentHeader Header(sbyte p, sbyte q) =>
        new(p, q, EpochBaseMs: 0, CreatedAtMs: Ts, FirstSequence: 0, PayloadSize: 0);

    private static SegmentLocation Loc(string stream) =>
        new("binance", "BTCUSDT", stream, Ts, 0, $"live-md/binance/BTCUSDT/{stream}/x.atft");

    private DailyTickCsvWriter TickWriter() => new(
        _storage, _tail, Options.Create(new HistoryLoaderStorageOptions { FlushEveryRows = 1, FlushIntervalSeconds = 60 }),
        NullLogger<DailyTickCsvWriter>.Instance, new WriteLockManager());

    [Fact]
    public async Task TradeProjection_WritesUnscaledDecimalRow()
    {
        var writer = TickWriter();
        var proj = new TradeProjection(writer, _map);
        await proj.Seed(Loc("trades"), Ct);

        // price 5000050 @ exp2 -> 50000.5 ; qty 123 @ exp3 -> 0.123 ; Sell -> is_buyer_maker 1 ; seq 77
        proj.Apply(new TradeTick(Ts, 5000050, 123, 77, AggressorSide.Sell), Header(2, 3), Loc("trades"));
        await proj.Flush(Ct);

        var assetDir = _map.Resolve("binance", "BTCUSDT");
        var key = Path.Combine(assetDir, "ticks",
            $"{DateTimeOffset.FromUnixTimeMilliseconds(Ts).UtcDateTime:yyyy-MM-dd}.csv");
        var lines = await _storage.ReadAllLines(key, Ct);
        Assert.Equal("ts,price,qty,is_buyer_maker,agg_id", lines[0]);
        Assert.Equal($"{Ts},50000.5,0.123,1,77", lines[1]);
    }
}
