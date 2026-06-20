using System.Globalization;

namespace AlgoTradeForge.Live.Relay;

public sealed class LocalFileSegmentSink(string root) : ITickSegmentSink
{
    public ValueTask<Stream> BeginSegment(string instrument, long firstSequence, long createdAtMs, CancellationToken ct = default)
    {
        var dir = Path.Combine(root, instrument);
        Directory.CreateDirectory(dir);
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{createdAtMs:D13}-{firstSequence:D19}.atft");
        var path = Path.Combine(dir, name);
        Stream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16);
        return ValueTask.FromResult(stream);
    }

    public ValueTask CompleteSegment(string instrument, Stream segment, CancellationToken ct = default)
    {
        segment.Dispose();
        return ValueTask.CompletedTask;
    }
}
