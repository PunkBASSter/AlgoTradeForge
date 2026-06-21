using AlgoTradeForge.Live.Relay;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class LocalSegmentSinkTests
{
    [Fact]
    public async Task BeginSegment_CreatesStreamUnderInstrumentAndStream()
    {
        var root = Path.Combine(Path.GetTempPath(), $"sink_{Guid.NewGuid():N}");
        try
        {
            var sink = new LocalSegmentSink(root);
            var segment = await sink.BeginSegment("trades", "ESZ5", 1, 1_700_000_000_000, TestContext.Current.CancellationToken);
            await segment.WriteAsync(new byte[] { 0xAB, 0xCD }, TestContext.Current.CancellationToken);
            await sink.CompleteSegment("trades", "ESZ5", segment, TestContext.Current.CancellationToken);

            var expectedDir = Path.Combine(root, "ESZ5", "trades");
            var files = Directory.GetFiles(expectedDir, "*.atft");
            Assert.Single(files);
            var bytes = await File.ReadAllBytesAsync(files[0], TestContext.Current.CancellationToken);
            Assert.Equal(new byte[] { 0xAB, 0xCD }, bytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
