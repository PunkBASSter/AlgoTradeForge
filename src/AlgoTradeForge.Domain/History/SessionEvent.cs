using System.Buffers.Binary;

namespace AlgoTradeForge.Domain.History;

public readonly record struct SessionEvent(long TimestampMs, SessionEventKind Kind)
    : IFramePayload<SessionEvent>
{
    public static string StreamName => "_session";
    public static int PayloadSize => 9;
    public long Sequence => 0;

    public int WriteTo(Span<byte> dest)
    {
        if (dest.Length < PayloadSize)
            throw new ArgumentException($"dest must be at least {PayloadSize} bytes", nameof(dest));

        BinaryPrimitives.WriteInt64LittleEndian(dest[0..], TimestampMs);
        dest[8] = (byte)Kind;
        return PayloadSize;
    }

    public static SessionEvent ReadFrom(ReadOnlySpan<byte> src) =>
        new(BinaryPrimitives.ReadInt64LittleEndian(src[0..]), (SessionEventKind)src[8]);

    public string Format() =>
        $"SESSION ts={TimestampMs} kind={Kind}";
}
