using System.Text;
using AlgoTradeForge.HistoryLoader.WebApi.Collection;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Collection;

public sealed class SpotAggTradeStreamServiceTests
{
    private static byte[] CombinedEnvelope(
        string symbol = "BTCUSDT",
        long ts = 1_700_000_000_000L,
        long aggId = 12345,
        string price = "50000.00",
        string qty = "0.01",
        bool isBuyerMaker = false)
    {
        var maker = isBuyerMaker ? "true" : "false";
        var json = $$"""
            {
              "stream": "{{symbol.ToLowerInvariant()}}@aggTrade",
              "data": {
                "e": "aggTrade",
                "E": {{ts}},
                "s": "{{symbol}}",
                "a": {{aggId}},
                "p": "{{price}}",
                "q": "{{qty}}",
                "f": 100,
                "l": 105,
                "T": {{ts}},
                "m": {{maker}},
                "M": true
              }
            }
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    private static byte[] RawAggTradeEvent(
        string symbol = "BTCUSDT",
        long ts = 1_700_000_000_000L,
        long aggId = 12345,
        string price = "50000.00",
        string qty = "0.01",
        bool isBuyerMaker = false)
    {
        var maker = isBuyerMaker ? "true" : "false";
        var json = $$"""
            {
              "e": "aggTrade",
              "E": {{ts}},
              "s": "{{symbol}}",
              "a": {{aggId}},
              "p": "{{price}}",
              "q": "{{qty}}",
              "f": 100,
              "l": 105,
              "T": {{ts}},
              "m": {{maker}},
              "M": true
            }
            """;
        return Encoding.UTF8.GetBytes(json);
    }

    [Fact]
    public void Parse_CombinedEnvelope_ReturnsRecord()
    {
        var data = CombinedEnvelope(
            symbol: "BTCUSDT",
            ts: 1_700_000_000_000L,
            aggId: 99999,
            price: "50250.50",
            qty: "0.0125",
            isBuyerMaker: true);

        var result = SpotAggTradeStreamService.ParseCombinedAggTrade(data);

        Assert.NotNull(result);
        var (symbol, record) = result.Value;
        Assert.Equal("BTCUSDT", symbol);
        Assert.Equal(1_700_000_000_000L, record.TimestampMs);
        Assert.Equal(50250.50, record.Values[0], precision: 10);
        Assert.Equal(0.0125, record.Values[1], precision: 10);
        Assert.Equal(1.0, record.Values[2]);
        Assert.Equal(99999.0, record.Values[3]);
    }

    [Fact]
    public void Parse_RawEventWithoutEnvelope_AlsoSucceeds()
    {
        var data = RawAggTradeEvent(symbol: "ETHUSDT", aggId: 42, price: "3000.00", qty: "1.5");

        var result = SpotAggTradeStreamService.ParseCombinedAggTrade(data);

        Assert.NotNull(result);
        var (symbol, record) = result.Value;
        Assert.Equal("ETHUSDT", symbol);
        Assert.Equal(3000.00, record.Values[0], precision: 10);
        Assert.Equal(42.0, record.Values[3]);
    }

    [Fact]
    public void Parse_BuyerMakerFalse_EncodedAsZero()
    {
        var data = CombinedEnvelope(isBuyerMaker: false);
        var result = SpotAggTradeStreamService.ParseCombinedAggTrade(data);
        Assert.NotNull(result);
        Assert.Equal(0.0, result.Value.Record.Values[2]);
    }

    [Fact]
    public void Parse_WrongEventType_ReturnsNull()
    {
        var json = """
            {
              "stream": "btcusdt@trade",
              "data": { "e": "trade", "s": "BTCUSDT", "T": 1, "a": 1, "p": "1", "q": "1", "m": false }
            }
            """;
        var result = SpotAggTradeStreamService.ParseCombinedAggTrade(Encoding.UTF8.GetBytes(json));
        Assert.Null(result);
    }

    [Fact]
    public void Parse_MalformedJson_ReturnsNull()
    {
        var data = Encoding.UTF8.GetBytes("not json {{");
        var result = SpotAggTradeStreamService.ParseCombinedAggTrade(data);
        Assert.Null(result);
    }

    [Fact]
    public void Parse_MissingPriceField_ReturnsNull()
    {
        var json = """
            {
              "data": { "e": "aggTrade", "s": "BTCUSDT", "T": 1, "a": 1, "q": "1", "m": false }
            }
            """;
        var result = SpotAggTradeStreamService.ParseCombinedAggTrade(Encoding.UTF8.GetBytes(json));
        Assert.Null(result);
    }
}
