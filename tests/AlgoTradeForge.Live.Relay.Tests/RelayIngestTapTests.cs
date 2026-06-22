using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class RelayIngestTapTests
{
    private sealed class FakeVenueConnector(IReadOnlyList<IMarketEvent> events) : IVenueConnector
    {
        public string Venue => "FAKE";
        public MarketDataSessionPolicy SessionPolicy => MarketDataSessionPolicy.Concurrent;
        public (sbyte PriceScaleExp, sbyte QtyScaleExp) InstrumentScale(string instrument) => (4, 3);

        public async IAsyncEnumerable<IMarketEvent> Stream(IReadOnlyList<string> instruments, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var ev in events)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return ev;
            }
        }
    }

    private sealed class RecordingTap : IRelayTradeTap
    {
        public List<(string Instrument, TradeTick Tick)> Calls { get; } = [];
        public void OnTrade(string instrument, in TradeTick tick) => Calls.Add((instrument, tick));
    }

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
    public async Task Pump_FansEveryArchivedTradeToTap_SameCountOrderAndValues()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"relay_tap_{Guid.NewGuid():N}");
        try
        {
            var t1 = new TradeTick(1_700_000_000_001, 5_000_000, 1, 1, AggressorSide.Buy);
            var t2 = new TradeTick(1_700_000_000_003, 5_000_500, 2, 2, AggressorSide.Sell);
            var t3 = new TradeTick(1_700_000_000_005, 4_999_500, 3, 3, AggressorSide.Buy);
            var quote = new QuoteTick(1_700_000_000_002, 4_999_000, 10, 5_001_000, 5, 1);

            var events = new IMarketEvent[]
            {
                new TradeEvent("ESZ5", t1),
                new QuoteEvent("ESZ5", quote),
                new TradeEvent("ESZ5", t2),
                new TradeEvent("ESZ5", t3),
            };

            var connector = new FakeVenueConnector(events);
            var sink = new LocalSegmentSink(dir);
            var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
            var options = new StreamPipelineOptions();
            var tap = new RecordingTap();

            await using (var writer = new RelayWriter("FAKE", sink, options, time, TimeSpan.FromSeconds(60)))
            {
                await RelayIngest.Pump(connector, writer, ["ESZ5"], tap, TestContext.Current.CancellationToken);
            }

            var archived = ReadAllFrames<TradeTick>(dir, "ESZ5");

            // Archival still happens — tap is additive, not a replacement.
            Assert.Equal(3, archived.Count);
            Assert.Equal([t1, t2, t3], archived);

            // Tap received exactly the archived trades, same order, same values.
            Assert.Equal(3, tap.Calls.Count);
            Assert.Equal(archived, tap.Calls.Select(c => c.Tick).ToList());
            Assert.All(tap.Calls, c => Assert.Equal("ESZ5", c.Instrument));

            // Quotes are not fanned to the trade tap.
            Assert.DoesNotContain(tap.Calls, c => c.Tick.TimestampMs == quote.TimestampMs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
