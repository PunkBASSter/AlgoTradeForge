using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

/// <summary>
/// Phase 2a: BinanceAggTradeParser must throw on malformed numeric fields rather than silently
/// dropping records. Matches the loud-failure stance of PartitionedSourceReader and the
/// CsvFeedSeriesLoader P1b-0a tightening. Retry helper does NOT loop on parse exceptions
/// (BinanceRetryHelper calls the parser outside its retry try/catch), so the throw is safe.
/// </summary>
public sealed class BinanceAggTradeParserTests
{
    [Fact]
    public void ParseBatch_ValidPayload_ReturnsAllRecordsInOrder()
    {
        var json = """
            [
                {"a":100,"p":"50000.5","q":"0.123","f":1,"l":1,"T":1700000000000,"m":false,"M":true},
                {"a":101,"p":"50001.0","q":"1.500","f":2,"l":2,"T":1700000001000,"m":true, "M":true}
            ]
            """;

        var records = BinanceAggTradeParser.ParseBatch(json);

        Assert.Equal(2, records.Length);

        Assert.Equal(1700000000000L, records[0].TimestampMs);
        Assert.Equal(50000.5, records[0].Values[0]);  // price
        Assert.Equal(0.123,   records[0].Values[1]);  // qty
        Assert.Equal(0.0,     records[0].Values[2]);  // is_buyer_maker (false)
        Assert.Equal(100.0,   records[0].Values[3]);  // agg_id

        Assert.Equal(1700000001000L, records[1].TimestampMs);
        Assert.Equal(1.0,            records[1].Values[2]);  // is_buyer_maker (true)
        Assert.Equal(101.0,          records[1].Values[3]);
    }

    [Fact]
    public void ParseBatch_MalformedPrice_ThrowsFormatExceptionWithAggId()
    {
        // Second record has non-numeric price; previously this would silently skip the record
        // and return an array of length 1 with no signal. Phase 2a fix: throw, including aggId
        // for forensics.
        var json = """
            [
                {"a":100,"p":"50000.5","q":"0.123","f":1,"l":1,"T":1700000000000,"m":false,"M":true},
                {"a":999,"p":"oops",   "q":"0.500","f":2,"l":2,"T":1700000001000,"m":true, "M":true}
            ]
            """;

        var ex = Assert.Throws<FormatException>(() => BinanceAggTradeParser.ParseBatch(json));
        Assert.Contains("999", ex.Message);
        Assert.Contains("'p'", ex.Message);
    }

    [Fact]
    public void ParseBatch_MalformedQty_ThrowsFormatExceptionWithAggId()
    {
        var json = """
            [{"a":42,"p":"50000.5","q":"NaN-ish","f":1,"l":1,"T":1700000000000,"m":false,"M":true}]
            """;

        var ex = Assert.Throws<FormatException>(() => BinanceAggTradeParser.ParseBatch(json));
        Assert.Contains("42", ex.Message);
        Assert.Contains("'q'", ex.Message);
    }

    [Fact]
    public void ParseBatch_EmptyArray_ReturnsEmpty()
    {
        var records = BinanceAggTradeParser.ParseBatch("[]");
        Assert.Empty(records);
    }
}
