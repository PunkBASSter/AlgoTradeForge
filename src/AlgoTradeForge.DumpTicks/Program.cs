using AlgoTradeForge.Live.Relay;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dump-ticks <segment.atft>");
    return 1;
}

var path = args[0];
var stream = Path.GetFileName(Path.GetDirectoryName(path))
    ?? throw new ArgumentException("Cannot determine stream name from path.");

var codec = FrameCodecRegistry.For(stream);

using var file = File.OpenRead(path);

Span<byte> headerBuf = stackalloc byte[SegmentHeader.Size];
file.ReadExactly(headerBuf);
var header = SegmentHeader.ReadFrom(headerBuf);
Console.WriteLine($"HEADER priceScaleExp={header.PriceScaleExp} qtyScaleExp={header.QtyScaleExp} " +
                  $"createdAtMs={header.CreatedAtMs} firstSeq={header.FirstSequence}");

var frameBuf = new byte[codec.PayloadSize];
long count = 0;
int read;
while ((read = file.ReadAtLeast(frameBuf, codec.PayloadSize, throwOnEndOfStream: false)) > 0)
{
    Console.WriteLine(codec.FormatFrame(frameBuf.AsSpan(0, read)));
    count++;
}
Console.WriteLine($"# {count} frames");
return 0;
