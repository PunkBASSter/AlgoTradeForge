using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class StreamPipelineTests
{
    private static List<T> ReadAllFrames<T>(string root, string instrument) where T : struct, IFramePayload<T>
    {
        var frames = new List<T>();
        var dir = Path.Combine(root, instrument, T.StreamName);
        if (!Directory.Exists(dir)) return frames;
        foreach (var path in Directory.GetFiles(dir, "*.atft").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var fs = File.OpenRead(path);
            using var reader = new SegmentReader<T>(fs);
            while (reader.TryRead(out var f)) frames.Add(f);
        }
        return frames;
    }

    [Fact]
    public async Task AllEnqueuedPayloads_PersistedInOrder_AcrossRotation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pipeline_{Guid.NewGuid():N}");
        try
        {
            var sink = new LocalSegmentSink(dir);
            var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
            // PayloadSize=33; header=64; rotation after header + 16 frames = 64 + 16*33 = 592 bytes
            var options = new StreamPipelineOptions { MaxSegmentBytes = 64 + 33 * 16 };

            await using (var pipeline = new StreamPipeline<TradeTick>(sink, options, time))
            {
                int id = pipeline.RegisterInstrument("ESZ5", priceScaleExp: 2, qtyScaleExp: 0);
                for (int i = 0; i < 200; i++)
                    await pipeline.Enqueue(id,
                        new TradeTick(1_700_000_000_000 + i, 5_000_000 + i, 1, i + 1, AggressorSide.Unknown),
                        TestContext.Current.CancellationToken);
            }

            var ticks = ReadAllFrames<TradeTick>(dir, "ESZ5");
            Assert.Equal(200, ticks.Count);
            for (int i = 0; i < 200; i++)
                Assert.Equal(i + 1, ticks[i].Sequence);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task SlowSink_AppliesBackpressure_NoDrops()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pipeline_{Guid.NewGuid():N}");
        try
        {
            var sink = new LocalSegmentSink(dir);
            var time = new FakeTimeProvider();
            var options = new StreamPipelineOptions { ChannelCapacity = 8 };

            await using (var pipeline = new StreamPipeline<TradeTick>(sink, options, time))
            {
                int id = pipeline.RegisterInstrument("NQZ5", 2, 0);
                for (int i = 0; i < 500; i++)
                    await pipeline.Enqueue(id,
                        new TradeTick(i, 100 + i, 1, i + 1, AggressorSide.Unknown),
                        TestContext.Current.CancellationToken);
                Assert.Equal(0, pipeline.DroppedCount);
            }

            var count = ReadAllFrames<TradeTick>(dir, "NQZ5").Count;
            Assert.Equal(500, count);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task QuoteTick_Pipeline_PersistsIndependently()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"pipeline_{Guid.NewGuid():N}");
        try
        {
            var sink = new LocalSegmentSink(dir);
            var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
            var options = new StreamPipelineOptions();

            await using (var pipeline = new StreamPipeline<QuoteTick>(sink, options, time))
            {
                int id = pipeline.RegisterInstrument("BTCUSDT", priceScaleExp: 2, qtyScaleExp: 8);
                for (int i = 0; i < 50; i++)
                    await pipeline.Enqueue(id,
                        new QuoteTick(1_700_000_000_000 + i, 50_000_000 + i, 10, 50_001_000 + i, 5, i + 1),
                        TestContext.Current.CancellationToken);
            }

            var quotes = ReadAllFrames<QuoteTick>(dir, "BTCUSDT");
            Assert.Equal(50, quotes.Count);
            for (int i = 0; i < 50; i++)
                Assert.Equal(i + 1, quotes[i].Sequence);

            // Verify persisted under the quotes stream subfolder
            Assert.True(Directory.Exists(Path.Combine(dir, "BTCUSDT", "quotes")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
