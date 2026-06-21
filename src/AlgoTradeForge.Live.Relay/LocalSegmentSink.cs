using System.Globalization;

namespace AlgoTradeForge.Live.Relay;

public sealed class LocalSegmentSink(string root) : ISegmentSink
{
    public ValueTask<Stream> BeginSegment(string streamName, string instrument, long firstSequence, long createdAtMs, CancellationToken ct = default)
    {
        var dir = Path.Combine(root, instrument, streamName);
        Directory.CreateDirectory(dir);
        var name = string.Create(CultureInfo.InvariantCulture, $"{createdAtMs:D13}-{firstSequence:D19}.atft");
        Stream s = new FileStream(Path.Combine(dir, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16);
        return ValueTask.FromResult(s);
    }

    public ValueTask CompleteSegment(string streamName, string instrument, Stream segment, CancellationToken ct = default)
    {
        segment.Dispose();
        return ValueTask.CompletedTask;
    }
}
