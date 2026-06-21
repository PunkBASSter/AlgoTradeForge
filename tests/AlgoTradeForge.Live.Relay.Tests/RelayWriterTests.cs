using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class RelayWriterTests
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
    public async Task WriteTrade_And_WriteQuote_LandInSeparateStreams()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_{Guid.NewGuid():N}");
        try
        {
            var sink = new LocalSegmentSink(dir);
            var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
            var options = new StreamPipelineOptions();

            await using (var writer = new RelayWriter("TEST", sink, options, time, TimeSpan.FromSeconds(30)))
            {
                await writer.Start(TestContext.Current.CancellationToken);
                int id = writer.RegisterInstrument("ESZ5", priceScaleExp: 2, qtyScaleExp: 0);
                await writer.WriteTrade(id, new TradeTick(1_700_000_000_001, 5_000_000, 1, 1, AggressorSide.Unknown), TestContext.Current.CancellationToken);
                await writer.WriteQuote(id, new QuoteTick(1_700_000_000_002, 4_999_000, 10, 5_001_000, 5, 1), TestContext.Current.CancellationToken);
            }

            var trades = ReadAllFrames<TradeTick>(dir, "ESZ5");
            Assert.Single(trades);
            Assert.Equal(5_000_000L, trades[0].Price);

            var quotes = ReadAllFrames<QuoteTick>(dir, "ESZ5");
            Assert.Single(quotes);
            Assert.Equal(4_999_000L, quotes[0].BidPrice);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Heartbeat_WrittenToSession_OnTimerAdvance()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_{Guid.NewGuid():N}");
        try
        {
            var sink = new LocalSegmentSink(dir);
            var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
            var options = new StreamPipelineOptions();
            var venue = "TESTV";

            await using (var writer = new RelayWriter(venue, sink, options, time, TimeSpan.FromSeconds(5)))
            {
                await writer.Start(TestContext.Current.CancellationToken);
                time.Advance(TimeSpan.FromSeconds(6));
                await Task.Yield();
                await writer.WaitForDrain();
            }

            var events = ReadAllFrames<SessionEvent>(dir, venue);
            Assert.Contains(events, e => e.Kind == SessionEventKind.SessionStart);
            Assert.Contains(events, e => e.Kind == SessionEventKind.Heartbeat);
            Assert.Contains(events, e => e.Kind == SessionEventKind.SessionEnd);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Dispose_WritesSessionEnd_AndFlushesAll()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_{Guid.NewGuid():N}");
        try
        {
            var sink = new LocalSegmentSink(dir);
            var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
            var options = new StreamPipelineOptions();
            var venue = "DISP";

            await using (var writer = new RelayWriter(venue, sink, options, time, TimeSpan.FromSeconds(60)))
            {
                await writer.Start(TestContext.Current.CancellationToken);
                int id = writer.RegisterInstrument("NQZ5", priceScaleExp: 2, qtyScaleExp: 0);
                await writer.WriteTrade(id, new TradeTick(1_700_000_000_001, 20_000_000, 2, 1, AggressorSide.Buy), TestContext.Current.CancellationToken);
            }

            var events = ReadAllFrames<SessionEvent>(dir, venue);
            // SessionStart must come before SessionEnd
            var startIdx = events.FindIndex(e => e.Kind == SessionEventKind.SessionStart);
            var endIdx = events.FindIndex(e => e.Kind == SessionEventKind.SessionEnd);
            Assert.True(startIdx >= 0, "SessionStart not found");
            Assert.True(endIdx >= 0, "SessionEnd not found");
            Assert.True(startIdx < endIdx, "SessionStart must precede SessionEnd");

            // Trade must be readable
            var trades = ReadAllFrames<TradeTick>(dir, "NQZ5");
            Assert.Single(trades);
            Assert.Equal(20_000_000L, trades[0].Price);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
