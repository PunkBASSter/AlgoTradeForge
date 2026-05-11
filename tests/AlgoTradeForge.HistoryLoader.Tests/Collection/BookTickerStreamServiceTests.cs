using System.Text;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class BookTickerStreamServiceTests
{
    private static byte[] FuturesEnvelope(
        string symbol = "BTCUSDT",
        long updateId = 400900217,
        string bidPrice = "25.35190000",
        string bidQty = "31.21000000",
        string askPrice = "25.36520000",
        string askQty = "40.66000000",
        long ts = 1_700_000_000_000L)
    {
        var json = $$"""
            {
              "stream": "{{symbol.ToLowerInvariant()}}@bookTicker",
              "data": {
                "e": "bookTicker",
                "u": {{updateId}},
                "s": "{{symbol}}",
                "b": "{{bidPrice}}",
                "B": "{{bidQty}}",
                "a": "{{askPrice}}",
                "A": "{{askQty}}",
                "T": {{ts}},
                "E": {{ts + 1}}
              }
            }
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    // Older spot bookTicker payloads omit T/E entirely — wall-clock fallback must apply.
    private static byte[] LegacySpotEvent(
        string symbol = "ETHUSDT",
        long updateId = 999,
        string bidPrice = "3000.00",
        string askPrice = "3000.50")
    {
        var json = $$"""
            {
              "u": {{updateId}},
              "s": "{{symbol}}",
              "b": "{{bidPrice}}",
              "B": "1.0",
              "a": "{{askPrice}}",
              "A": "1.5"
            }
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    [Fact]
    public void Parse_Envelope_ExtractsAllFields()
    {
        var data = FuturesEnvelope(
            symbol: "BTCUSDT",
            updateId: 400900217,
            bidPrice: "25.35190000",
            bidQty: "31.21000000",
            askPrice: "25.36520000",
            askQty: "40.66000000",
            ts: 1_700_000_000_000L);

        var result = BookTickerStreamService.ParseBookTicker(data);

        Assert.NotNull(result);
        var (symbol, record) = result.Value;
        Assert.Equal("BTCUSDT", symbol);
        Assert.Equal(1_700_000_000_000L, record.TimestampMs);
        Assert.Equal(25.3519, record.Values[0], precision: 4);
        Assert.Equal(31.21, record.Values[1], precision: 4);
        Assert.Equal(25.3652, record.Values[2], precision: 4);
        Assert.Equal(40.66, record.Values[3], precision: 4);
        Assert.Equal(400900217.0, record.Values[4]);
    }

    [Fact]
    public void Parse_LegacySpotWithoutTimestamp_FallsBackToWallClock()
    {
        var before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = BookTickerStreamService.ParseBookTicker(LegacySpotEvent());
        var after = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Assert.NotNull(result);
        Assert.InRange(result.Value.Record.TimestampMs, before, after);
    }

    [Fact]
    public void Parse_MissingBidPrice_ReturnsNull()
    {
        var json = """
            {
              "data": {"u":1,"s":"BTCUSDT","B":"1","a":"100","A":"1","T":1}
            }
            """;
        var result = BookTickerStreamService.ParseBookTicker(Encoding.UTF8.GetBytes(json));
        Assert.Null(result);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsNull()
    {
        var result = BookTickerStreamService.ParseBookTicker(Encoding.UTF8.GetBytes("garbage {{"));
        Assert.Null(result);
    }
}
