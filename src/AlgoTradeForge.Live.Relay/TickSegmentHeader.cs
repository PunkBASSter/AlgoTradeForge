using System.Buffers.Binary;

namespace AlgoTradeForge.Live.Relay;

public readonly record struct TickSegmentHeader(
    sbyte PriceScaleExp,
    sbyte QtyScaleExp,
    long EpochBaseMs,
    long CreatedAtMs,
    long FirstSequence)
{
    public void WriteTo(Span<byte> dest)
    {
        if (dest.Length < RelayFormat.HeaderSize)
            throw new ArgumentException($"Header buffer must be >= {RelayFormat.HeaderSize} bytes.", nameof(dest));

        dest[..RelayFormat.HeaderSize].Clear();
        RelayFormat.Magic.CopyTo(dest);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[4..], RelayFormat.CurrentVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[6..], RelayFormat.FrameSize);
        dest[8] = (byte)PriceScaleExp;
        dest[9] = (byte)QtyScaleExp;
        BinaryPrimitives.WriteInt64LittleEndian(dest[16..], EpochBaseMs);
        BinaryPrimitives.WriteInt64LittleEndian(dest[24..], CreatedAtMs);
        BinaryPrimitives.WriteInt64LittleEndian(dest[32..], FirstSequence);
    }

    public static TickSegmentHeader ReadFrom(ReadOnlySpan<byte> src)
    {
        if (src.Length < RelayFormat.HeaderSize)
            throw new ArgumentException($"Header buffer must be >= {RelayFormat.HeaderSize} bytes.", nameof(src));
        if (!src[..4].SequenceEqual(RelayFormat.Magic))
            throw new InvalidDataException("Not an ATFT tick segment (bad magic).");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(src[4..]);
        if (version != RelayFormat.CurrentVersion)
            throw new InvalidDataException($"Unsupported ATFT version {version}.");

        var frameSize = BinaryPrimitives.ReadUInt16LittleEndian(src[6..]);
        if (frameSize != RelayFormat.FrameSize)
            throw new InvalidDataException($"Unexpected frame size {frameSize}.");

        return new TickSegmentHeader(
            PriceScaleExp: (sbyte)src[8],
            QtyScaleExp: (sbyte)src[9],
            EpochBaseMs: BinaryPrimitives.ReadInt64LittleEndian(src[16..]),
            CreatedAtMs: BinaryPrimitives.ReadInt64LittleEndian(src[24..]),
            FirstSequence: BinaryPrimitives.ReadInt64LittleEndian(src[32..]));
    }
}
