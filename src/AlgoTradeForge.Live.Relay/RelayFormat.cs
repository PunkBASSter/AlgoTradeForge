namespace AlgoTradeForge.Live.Relay;

public static class RelayFormat
{
    public const int HeaderSize = 64;
    public const int FrameSize = 40;
    public const ushort CurrentVersion = 1;

    public static ReadOnlySpan<byte> Magic => "ATFT"u8;
}
