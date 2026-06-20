using AlgoTradeForge.Live.Relay;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: dump-ticks <segment.atft>");
    return 1;
}

using var file = File.OpenRead(args[0]);
using var reader = new TickSegmentReader(file);

var h = reader.Header;
Console.WriteLine($"HEADER priceScaleExp={h.PriceScaleExp} qtyScaleExp={h.QtyScaleExp} " +
                  $"createdAtMs={h.CreatedAtMs} firstSeq={h.FirstSequence}");

long count = 0;
while (reader.TryReadFrame(out var frame))
{
    Console.WriteLine(RelayFrameFormatter.Format(frame));
    count++;
}
Console.WriteLine($"# {count} frames");
return 0;
