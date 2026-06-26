using AlgoTradeForge.Domain;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.LiveHost.Application.Live.Recovery;
using AlgoTradeForge.LiveHost.Infrastructure.Live.Recovery;
using AlgoTradeForge.Storage;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.Recovery;

public class RelayArchiveReplaySourceTests : IDisposable
{
    private readonly TempStorage _tmp = new();

    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Replays_atft_segments_from_boundary_in_aggId_order()
    {
        var ct = TestContext.Current.CancellationToken;
        const string venue = "binance";
        const string instrument = "BTCUSDT";
        const string prefix = "relay";

        await WriteSegment(_tmp.Storage, $"{prefix}/{venue}/{instrument}/trades",
            createdAtMs: 1000, firstSequence: 10,
            [Tick(10, 1000), Tick(11, 1001), Tick(12, 1002)]);

        var src = new RelayArchiveReplaySource(_tmp.Storage, prefix);
        var req = new ReplayRequest(Btc(), venue, "ticks", FromTs: 1001);

        var seqs = new List<long>();
        await foreach (var t in src.Replay(req, ct))
            seqs.Add(t.Sequence);

        Assert.Equal([11L, 12L], seqs);
    }

    [Fact]
    public async Task Replays_multiple_segments_in_chronological_order()
    {
        var ct = TestContext.Current.CancellationToken;
        const string venue = "binance";
        const string instrument = "BTCUSDT";
        const string prefix = "relay";

        // Write segment 2 first to confirm sorting is ordinal on filename, not insertion order.
        await WriteSegment(_tmp.Storage, $"{prefix}/{venue}/{instrument}/trades",
            createdAtMs: 2000, firstSequence: 20,
            [Tick(20, 2000), Tick(21, 2001)]);
        await WriteSegment(_tmp.Storage, $"{prefix}/{venue}/{instrument}/trades",
            createdAtMs: 1000, firstSequence: 10,
            [Tick(10, 1000), Tick(11, 1001)]);

        var src = new RelayArchiveReplaySource(_tmp.Storage, prefix);
        var req = new ReplayRequest(Btc(), venue, "ticks", FromTs: 0);

        var seqs = new List<long>();
        await foreach (var t in src.Replay(req, ct))
            seqs.Add(t.Sequence);

        Assert.Equal([10L, 11L, 20L, 21L], seqs);
    }

    [Fact]
    public async Task Returns_empty_when_no_segments_exist()
    {
        var ct = TestContext.Current.CancellationToken;
        var src = new RelayArchiveReplaySource(_tmp.Storage, "relay");
        var req = new ReplayRequest(Btc(), "binance", "ticks", FromTs: 0);

        var seqs = new List<long>();
        await foreach (var t in src.Replay(req, ct))
            seqs.Add(t.Sequence);

        Assert.Empty(seqs);
    }

    // --- helpers ---

    private static Asset Btc() =>
        CryptoPerpetualAsset.Create("BTCUSDT", "binance", decimalDigits: 2);

    private static TradeTick Tick(long seq, long ts = 0) =>
        new(ts, Price: 100, Quantity: 1, Sequence: seq, Aggressor: AggressorSide.Buy);

    private static async Task WriteSegment(
        IFileStorage storage,
        string dirKey,
        long createdAtMs,
        long firstSequence,
        TradeTick[] ticks)
    {
        using var ms = new MemoryStream();
        // SegmentWriter requires a SegmentHeader; PayloadSize must equal TradeTick.PayloadSize
        // so SegmentReader's validation passes. PriceScaleExp/QtyScaleExp/EpochBaseMs are
        // metadata-only — SegmentReader does not validate them, so zero is fine for tests.
        var header = new SegmentHeader(
            PriceScaleExp: 0,
            QtyScaleExp: 0,
            EpochBaseMs: 0,
            CreatedAtMs: createdAtMs,
            FirstSequence: firstSequence,
            PayloadSize: (ushort)TradeTick.PayloadSize);
        using var writer = new SegmentWriter<TradeTick>(ms, in header, leaveOpen: true);
        foreach (var t in ticks) writer.Write(in t);
        writer.Dispose(); // flush before ToArray
        var name = $"{createdAtMs:D13}-{firstSequence:D19}.atft";
        await storage.WriteAllBytes($"{dirKey}/{name}", ms.ToArray().AsMemory());
    }
}

/// <summary>
/// Wraps <see cref="LocalFileStorage"/> over a temp directory for test isolation.
/// </summary>
internal sealed class TempStorage : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"atf-test-{Guid.NewGuid():N}");

    public IFileStorage Storage { get; }

    public TempStorage()
    {
        Directory.CreateDirectory(_root);
        Storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
