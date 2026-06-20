using System.Buffers.Binary;

namespace AlgoTradeForge.Live.Relay;

public readonly record struct SegmentHeader(
    sbyte PriceScaleExp,
    sbyte QtyScaleExp,
    long EpochBaseMs,
    long CreatedAtMs,
    long FirstSequence,
    ushort PayloadSize)
{
    public const int Size = 64;
    public const ushort Version = 1;

    public static ReadOnlySpan<byte> Magic => "ATFT"u8;

    public void WriteTo(Span<byte> dest)
    {
        if (dest.Length < Size)
            throw new ArgumentException($"Header buffer must be >= {Size} bytes.", nameof(dest));

        dest[..Size].Clear();
        Magic.CopyTo(dest);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..], PayloadSize);
        dest[8] = (byte)PriceScaleExp;
        dest[9] = (byte)QtyScaleExp;
        // bytes 10..16 reserved
        BinaryPrimitives.WriteInt64LittleEndian(dest[16..], EpochBaseMs);
        BinaryPrimitives.WriteInt64LittleEndian(dest[24..], CreatedAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(dest[32..], FirstSequence);
        // bytes 40..64 reserved
    }

    public static SegmentHeader ReadFrom(ReadOnlySpan<byte> src)
    {
        if (src.Length < Size)
            throw new ArgumentException($"Header buffer must be >= {Size} bytes.", nameof(src));
        if (!src[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Not an ATFT segment (bad magic).");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]);
        if (version != Version)
            throw new InvalidDataException($"Unsupported ATFT version {version}.");

        return new SegmentHeader(
            PriceScaleExp: (sbyte)src[8],
            QtyScaleExp: (sbyte)src[9],
            EpochBaseMs: BinaryPrimitives.ReadInt64LittleEndian(src[16..]),
            CreatedAtMs: BinaryPrimitives.ReadInt64LittleEndian(src[24..]),
            FirstSequence: BinaryPrimitives.ReadInt64LittleEndian(src[32..]),
            PayloadSize: BinaryPrimitives.ReadUInt16LittleEndian(src[6..]));
    }
}
