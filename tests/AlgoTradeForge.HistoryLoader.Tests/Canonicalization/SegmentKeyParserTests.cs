using AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Canonicalization;

public sealed class SegmentKeyParserTests
{
    [Fact]
    public void TryParse_TradesKey_ExtractsAllParts()
    {
        var key = "live-md/binance/BTCUSDT/trades/1700000000000-0000000000000012345.atft";
        Assert.True(SegmentKeyParser.TryParse(key, "live-md", out var loc));
        Assert.Equal("binance", loc.Venue);
        Assert.Equal("BTCUSDT", loc.InstrumentOrVenue);
        Assert.Equal("trades", loc.StreamName);
        Assert.Equal(1700000000000, loc.CreatedAtMs);
        Assert.Equal(12345, loc.FirstSequence);
        Assert.Equal(key, loc.Key);
    }

    [Fact]
    public void TryParse_SessionKey_VenueOccupiesInstrumentSlot()
    {
        var key = "live-md/binance/binance/_session/1700000000000-0000000000000000000.atft";
        Assert.True(SegmentKeyParser.TryParse(key, "live-md", out var loc));
        Assert.Equal("binance", loc.Venue);
        Assert.Equal("binance", loc.InstrumentOrVenue);
        Assert.Equal("_session", loc.StreamName);
        Assert.Equal(0, loc.FirstSequence);
    }

    [Theory]
    [InlineData("live-md/binance/BTCUSDT/trades/not-a-segment.txt")]
    [InlineData("live-md/binance/BTCUSDT/trades")]
    [InlineData("other-prefix/binance/BTCUSDT/trades/0001700000000-0000000000000012345.atft")]
    public void TryParse_Malformed_ReturnsFalse(string key)
    {
        Assert.False(SegmentKeyParser.TryParse(key, "live-md", out _));
    }
}
