using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public sealed class FrameCodec<T> : IFrameCodec where T : struct, IFramePayload<T>
{
    public string StreamName => T.StreamName;
    public int PayloadSize => T.PayloadSize;
    public string FormatFrame(ReadOnlySpan<byte> payload) => T.ReadFrom(payload).Format();
}
