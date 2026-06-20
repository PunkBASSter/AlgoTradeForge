using System.Buffers.Binary;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class TickSegmentReader : IDisposable
{
    private readonly Stream _src;
    private readonly bool _leaveOpen;
    private readonly byte[] _frame = new byte[RelayFormat.FrameSize];

    public TickSegmentHeader Header { get; }

    public TickSegmentReader(Stream source, bool leaveOpen = false)
    {
        _src = source;
        _leaveOpen = leaveOpen;

        Span<byte> hbuf = stackalloc byte[RelayFormat.HeaderSize];
        _src.ReadExactly(hbuf);
        Header = TickSegmentHeader.ReadFrom(hbuf);
    }

    public bool TryReadFrame(out RelayFrame frame)
    {
        int n = _src.ReadAtLeast(_frame, RelayFormat.FrameSize, throwOnEndOfStream: false);
        if (n == 0) { frame = default; return false; }
        if (n < RelayFormat.FrameSize) throw new EndOfStreamException("Torn relay frame.");

        var b = _frame.AsSpan();
        var type = (FrameType)b[0];
        byte reason = b[1];
        long ts = BinaryPrimitives.ReadInt64LittleEndian(b[8..]);

        if (type == FrameType.Tick)
        {
            var trade = new TradeTick(
                ts,
                BinaryPrimitives.ReadInt64LittleEndian(b[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(b[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(b[32..]),
                (AggressorSide)reason);
            frame = new RelayFrame(type, ts, trade, 0);
        }
        else
        {
            frame = new RelayFrame(type, ts, default, reason);
        }
        return true;
    }

    public void Dispose()
    {
        if (!_leaveOpen) _src.Dispose();
    }
}
