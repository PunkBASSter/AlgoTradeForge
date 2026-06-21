namespace AlgoTradeForge.Domain.History;

public interface IFramePayload<TSelf> where TSelf : IFramePayload<TSelf>
{
    static abstract string StreamName { get; }
    static abstract int PayloadSize { get; }
    long TimestampMs { get; }
    long Sequence { get; }
    int  WriteTo(Span<byte> dest);
    static abstract TSelf ReadFrom(ReadOnlySpan<byte> src);
    string Format();
}
