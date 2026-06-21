using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Live.Relay;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AlgoTradeForge.Live.Relay.Tests;

public class VenueIngestTests
{
    private sealed class FakeVenueConnector(IReadOnlyList<IMarketEvent> events) : IVenueConnector
    {
        public string Venue => "FAKE";
        public MarketDataSessionPolicy SessionPolicy => MarketDataSessionPolicy.Concurrent;

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
    public async Task Pump_WritesTradeAndQuoteToSeparateStreams()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"venue_ingest_{Guid.NewGuid():N}");
        try
        {
            var trade = new TradeTick(1_700_000_000_001, 5_000_000, 1, 1, AggressorSide.Buy);
            var quote = new QuoteTick(1_700_000_000_002, 4_999_000, 10, 5_001_000, 5, 1);

            var events = new IMarketEvent[]
            {
                new TradeEvent("ESZ5", trade),
                new QuoteEvent("ESZ5", quote),
            };

            var connector = new FakeVenueConnector(events);
            var sink = new LocalSegmentSink(dir);
            var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
            var options = new StreamPipelineOptions();

            await using (var writer = new RelayWriter("FAKE", sink, options, time, TimeSpan.FromSeconds(60)))
            {
                await RelayIngest.Pump(connector, writer, ["ESZ5"], TestContext.Current.CancellationToken);
            }

            var trades = ReadAllFrames<TradeTick>(dir, "ESZ5");
            Assert.Single(trades);
            Assert.Equal(trade, trades[0]);

            var quotes = ReadAllFrames<QuoteTick>(dir, "ESZ5");
            Assert.Single(quotes);
            Assert.Equal(quote, quotes[0]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
