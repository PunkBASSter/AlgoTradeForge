using System.Buffers.Binary;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class TickSegmentWriter : IDisposable
{
    private readonly Stream _dest;
    private readonly bool _leaveOpen;
    private readonly byte[] _frame = new byte[RelayFormat.FrameSize];
    private bool _disposed;

    public TickSegmentWriter(Stream destination, in TickSegmentHeader header, bool leaveOpen = false)
    {
        _dest = destination;
        _leaveOpen = leaveOpen;

        Span<byte> hbuf = stackalloc byte[RelayFormat.HeaderSize];
        header.WriteTo(hbuf);
        _dest.Write(hbuf);
    }

    public void WriteTick(in TradeTick tick)
    {
        var b = _frame.AsSpan();
        b.Clear();
        b[0] = (byte)FrameType.Tick;
        b[1] = (byte)tick.Aggressor;
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], tick.TimestampMs);
        BinaryPrimitives.WriteInt64LittleEndian(b[16..], tick.Price);
        BinaryPrimitives.WriteInt64LittleEndian(b[24..], tick.Quantity);
        BinaryPrimitives.WriteInt64LittleEndian(b[32..], tick.Sequence);
        _dest.Write(_frame, 0, RelayFormat.FrameSize);
    }

    public void WriteHeartbeat(long timestampMs) => WriteMarker(FrameType.Heartbeat, timestampMs, 0);

    public void WriteSessionBoundary(long timestampMs, SessionBoundaryReason reason) =>
        WriteMarker(FrameType.SessionBoundary, timestampMs, (byte)reason);

    private void WriteMarker(FrameType type, long timestampMs, byte reason)
    {
        var b = _frame.AsSpan();
        b.Clear();
        b[0] = (byte)type;
        b[1] = reason;
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], timestampMs);
        _dest.Write(_frame, 0, RelayFormat.FrameSize);
    }

    public void Flush(bool toDisk)
    {
        if (toDisk && _dest is FileStream fs) fs.Flush(flushToDisk: true);
        else _dest.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dest.Flush();
        if (!_leaveOpen) _dest.Dispose();
    }
}
