using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class TickRelayWriterTests
{
    private static List<RelayFrame> ReadAll(string dir, string instrument)
    {
        var frames = new List<RelayFrame>();
        var instrDir = Path.Combine(dir, instrument);
        foreach (var path in Directory.GetFiles(instrDir, "*.atft").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var fs = File.OpenRead(path);
            using var reader = new TickSegmentReader(fs);
            while (reader.TryReadFrame(out var f)) frames.Add(f);
        }
        return frames;
    }

    [Fact]
    public async Task AllEnqueuedTicks_ArePersistedInOrder_AcrossRotation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_{Guid.NewGuid():N}");
        var sink = new LocalFileSegmentSink(dir);
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));

        // MaxSegmentBytes tiny so 200 ticks force several rotations (header 64 + 40/frame).
        var options = new TickRelayOptions { MaxSegmentBytes = 64 + 40 * 16 };

        await using (var writer = new TickRelayWriter(sink, options, time))
        {
            int id = writer.RegisterInstrument("ESZ5", priceScaleExp: 2, qtyScaleExp: 0);
            for (int i = 0; i < 200; i++)
                await writer.Enqueue(id, new TradeTick(1_700_000_000_000 + i, 5_000_000 + i, 1, i + 1, AggressorSide.Unknown), TestContext.Current.CancellationToken);
        }

        var ticks = ReadAll(dir, "ESZ5").Where(f => f.Type == FrameType.Tick).ToList();
        Assert.Equal(200, ticks.Count);
        for (int i = 0; i < 200; i++)
            Assert.Equal(i + 1, ticks[i].Trade.Sequence);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task SlowSink_AppliesBackpressure_WithoutDroppingViaEnqueue()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_{Guid.NewGuid():N}");
        var sink = new LocalFileSegmentSink(dir);
        var time = new FakeTimeProvider();
        var options = new TickRelayOptions { ChannelCapacity = 8 };

        await using (var writer = new TickRelayWriter(sink, options, time))
        {
            int id = writer.RegisterInstrument("NQZ5", 2, 0);
            for (int i = 0; i < 500; i++)
                await writer.Enqueue(id, new TradeTick(i, 100 + i, 1, i + 1, AggressorSide.Unknown), TestContext.Current.CancellationToken);
            Assert.Equal(0, writer.DroppedCount);
        }

        var ticks = ReadAll(dir, "NQZ5").Count(f => f.Type == FrameType.Tick);
        Assert.Equal(500, ticks);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Heartbeat_IsWritten_WhenTimerAdvances()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_{Guid.NewGuid():N}");
        var sink = new LocalFileSegmentSink(dir);
        var time = new FakeTimeProvider();
        var options = new TickRelayOptions { HeartbeatInterval = TimeSpan.FromSeconds(5) };

        await using (var writer = new TickRelayWriter(sink, options, time))
        {
            int id = writer.RegisterInstrument("CLZ5", 2, 0);
            await writer.Enqueue(id, new TradeTick(1, 100, 1, 1, AggressorSide.Unknown), TestContext.Current.CancellationToken);
            await writer.WaitForDrain();              // ensure the tick (and SessionStart) are written
            time.Advance(TimeSpan.FromSeconds(6));     // fire one heartbeat tick
            await writer.WaitForDrain();
        }

        var frames = ReadAll(dir, "CLZ5");
        Assert.Contains(frames, f => f.Type == FrameType.Heartbeat);
        Assert.Contains(frames, f => f.Type == FrameType.SessionBoundary &&
                                     f.ReasonCode == (byte)SessionBoundaryReason.SessionStart);
        Assert.Contains(frames, f => f.Type == FrameType.SessionBoundary &&
                                     f.ReasonCode == (byte)SessionBoundaryReason.SessionEnd);

        Directory.Delete(dir, recursive: true);
    }
}
