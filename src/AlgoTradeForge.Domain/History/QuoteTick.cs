using System.Buffers.Binary;

namespace AlgoTradeForge.Domain.History;

public readonly record struct QuoteTick(
    long TimestampMs, long BidPrice, long BidSize, long AskPrice, long AskSize, long Sequence)
    : IFramePayload<QuoteTick>
{
    public static string StreamName => "quotes";
    public static int PayloadSize => 48;
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);

    public int WriteTo(Span<byte> dest)
    {
        if (dest.Length < PayloadSize)
            throw new ArgumentException($"dest must be at least {PayloadSize} bytes", nameof(dest));

        BinaryPrimitives.WriteInt64LittleEndian(dest[0..], TimestampMs);
        BinaryPrimitives.WriteInt64LittleEndian(dest[8..], BidPrice);
        BinaryPrimitives.WriteInt64LittleEndian(dest[16..], BidSize);
        BinaryPrimitives.WriteInt64LittleEndian(dest[24..], AskPrice);
        BinaryPrimitives.WriteInt64LittleEndian(dest[32..], AskSize);
        BinaryPrimitives.WriteInt64LittleEndian(dest[40..], Sequence);
        return PayloadSize;
    }

    public static QuoteTick ReadFrom(ReadOnlySpan<byte> src) =>
        new(
            BinaryPrimitives.ReadInt64LittleEndian(src[0..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(src[40..]));

    public string Format() =>
        $"QUOTE ts={TimestampMs} bid={BidPrice}@{BidSize} ask={AskPrice}@{AskSize} seq={Sequence}";
}
