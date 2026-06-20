using System.Buffers.Binary;

namespace AlgoTradeForge.Domain.History;

public readonly record struct TradeTick(
    long TimestampMs,
    long Price,
    long Quantity,
    long Sequence,
    AggressorSide Aggressor) : IFramePayload<TradeTick>
{
    public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);

    public static string StreamName => "trades";
    public static int PayloadSize => 33;

    public int WriteTo(Span<byte> dest)
    {
        if (dest.Length < PayloadSize)
            throw new ArgumentException($"dest must be at least {PayloadSize} bytes", nameof(dest));

        BinaryPrimitives.WriteInt64LittleEndian(dest[0..], TimestampMs);
        BinaryPrimitives.WriteInt64LittleEndian(dest[8..], Price);
        BinaryPrimitives.WriteInt64LittleEndian(dest[16..], Quantity);
        BinaryPrimitives.WriteInt64LittleEndian(dest[24..], Sequence);
        dest[32] = (byte)Aggressor;
        return PayloadSize;
    }

    public static TradeTick ReadFrom(ReadOnlySpan<byte> src)
    {
        var ts  = BinaryPrimitives.ReadInt64LittleEndian(src[0..]);
        var price = BinaryPrimitives.ReadInt64LittleEndian(src[8..]);
        var qty = BinaryPrimitives.ReadInt64LittleEndian(src[16..]);
        var seq = BinaryPrimitives.ReadInt64LittleEndian(src[24..]);
        var agg = (AggressorSide)src[32];
        return new TradeTick(ts, price, qty, seq, agg);
    }

    public string Format() =>
        $"TRADE ts={TimestampMs} price={Price} qty={Quantity} seq={Sequence} aggressor={Aggressor}";
}
