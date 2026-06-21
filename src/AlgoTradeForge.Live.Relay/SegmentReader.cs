using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class SegmentReader<T> : IDisposable where T : IFramePayload<T>
{
    private readonly Stream _src;
    private readonly bool _leaveOpen;
    private readonly byte[] _buf = new byte[T.PayloadSize];

    public SegmentHeader Header { get; }

    public SegmentReader(Stream src, bool leaveOpen = false)
    {
        _src = src;
        _leaveOpen = leaveOpen;

        Span<byte> hbuf = stackalloc byte[SegmentHeader.Size];
        _src.ReadExactly(hbuf);
        Header = SegmentHeader.ReadFrom(hbuf);

        if (Header.PayloadSize != T.PayloadSize)
            throw new InvalidDataException(
                $"Segment payload size {Header.PayloadSize} does not match {typeof(T).Name}.PayloadSize ({T.PayloadSize}).");
    }

    public bool TryRead(out T payload)
    {
        int n = _src.ReadAtLeast(_buf, T.PayloadSize, throwOnEndOfStream: false);
        if (n == 0) { payload = default!; return false; }
        if (n < T.PayloadSize) throw new EndOfStreamException("Torn segment frame.");
        payload = T.ReadFrom(_buf.AsSpan());
        return true;
    }

    public void Dispose()
    {
        if (!_leaveOpen) _src.Dispose();
    }
}
