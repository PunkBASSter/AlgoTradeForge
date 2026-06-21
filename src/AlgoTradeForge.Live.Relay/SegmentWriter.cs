using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class SegmentWriter<T> : IDisposable where T : IFramePayload<T>
{
    private readonly Stream _dest;
    private readonly bool _leaveOpen;
    private readonly byte[] _buf = new byte[T.PayloadSize];
    private bool _disposed;

    public SegmentWriter(Stream dest, in SegmentHeader header, bool leaveOpen = false)
    {
        _dest = dest;
        _leaveOpen = leaveOpen;

        Span<byte> hbuf = stackalloc byte[SegmentHeader.Size];
        header.WriteTo(hbuf);
        _dest.Write(hbuf);
    }

    public void Write(in T payload)
    {
        payload.WriteTo(_buf.AsSpan());
        _dest.Write(_buf, 0, T.PayloadSize);
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
