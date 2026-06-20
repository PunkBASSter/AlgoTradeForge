namespace AlgoTradeForge.Live.Relay;

public interface ISegmentSink
{
    ValueTask<Stream> BeginSegment(string streamName, string instrument, long firstSequence, long createdAtMs, CancellationToken ct = default);
    ValueTask CompleteSegment(string streamName, string instrument, Stream segment, CancellationToken ct = default);
}
