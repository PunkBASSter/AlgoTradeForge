namespace AlgoTradeForge.Live.Relay;

public interface IFrameCodec
{
    string StreamName { get; }
    int PayloadSize { get; }
    string FormatFrame(ReadOnlySpan<byte> payload);
}
