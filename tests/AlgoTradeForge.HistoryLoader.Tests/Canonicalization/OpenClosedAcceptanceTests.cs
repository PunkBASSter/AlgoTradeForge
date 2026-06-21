using System.Buffers.Binary;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

// A brand-new event type defined ONLY in the test project.
public readonly record struct DepthTick(long TimestampMs, long Level, long Price, long Size, long Sequence)
    : IFramePayload<DepthTick>
{
    public static string StreamName => "depth";
    public static int PayloadSize => 40;
    public int WriteTo(Span<byte> d)
    {
        BinaryPrimitives.WriteInt64LittleEndian(d[0..], TimestampMs);
        BinaryPrimitives.WriteInt64LittleEndian(d[8..], Level);
        BinaryPrimitives.WriteInt64LittleEndian(d[16..], Price);
        BinaryPrimitives.WriteInt64LittleEndian(d[24..], Size);
        BinaryPrimitives.WriteInt64LittleEndian(d[32..], Sequence);
        return PayloadSize;
    }
    public static DepthTick ReadFrom(ReadOnlySpan<byte> s) => new(
        BinaryPrimitives.ReadInt64LittleEndian(s[0..]),
        BinaryPrimitives.ReadInt64LittleEndian(s[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(s[16..]),
        BinaryPrimitives.ReadInt64LittleEndian(s[24..]),
        BinaryPrimitives.ReadInt64LittleEndian(s[32..]));
    public string Format() => $"DEPTH ts={TimestampMs} lvl={Level} px={Price} sz={Size} seq={Sequence}";
}

// A throwaway projection that records what it received — no production writer needed.
internal sealed class DepthProjection : IStreamProjection<DepthTick>
{
    public List<DepthTick> Received { get; } = new();
    public void Apply(in DepthTick frame, in SegmentHeader header, SegmentLocation loc) => Received.Add(frame);
    public Task Seed(SegmentLocation loc, CancellationToken ct) => Task.CompletedTask;
    public Task Flush(CancellationToken ct) => Task.CompletedTask;
}

public sealed class OpenClosedAcceptanceTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStorage _storage;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public OpenClosedAcceptanceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"OpenClosed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _storage = new LocalFileStorage(new LocalStorageOptions { DataRoot = _root });
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    [Fact]
    public async Task NewStreamType_CanonicalizesWithZeroProductionEdits()
    {
        long ts = 1_700_000_000_000;
        using var ms = new MemoryStream();
        using (var w = new SegmentWriter<DepthTick>(ms,
            new SegmentHeader(2, 3, 0, ts, 0, (ushort)DepthTick.PayloadSize), leaveOpen: true))
            w.Write(new DepthTick(ts, 0, 5000000, 100, 1));
        await _storage.WriteAllBytes($"live-md/binance/BTCUSDT/depth/{ts:D13}-{0:D19}.atft", ms.ToArray(), Ct);

        var proj = new DepthProjection();
        var canon = new StreamCanonicalizer<DepthTick>(
            _storage, proj, new FileStreamCursorStore(_storage), "live-md", "_canon-cursors");

        var n = await canon.Run("binance", "BTCUSDT", Ct);

        Assert.Equal(1, n);
        Assert.Single(proj.Received);
        Assert.Equal("depth", canon.StreamName);
    }
}
