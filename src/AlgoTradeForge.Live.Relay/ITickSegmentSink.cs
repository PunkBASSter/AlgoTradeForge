namespace AlgoTradeForge.Live.Relay;

public interface ITickSegmentSink
{
    ValueTask<Stream> BeginSegment(string instrument, long firstSequence, long createdAtMs, CancellationToken ct = default);
    ValueTask CompleteSegment(string instrument, Stream segment, CancellationToken ct = default);
}
