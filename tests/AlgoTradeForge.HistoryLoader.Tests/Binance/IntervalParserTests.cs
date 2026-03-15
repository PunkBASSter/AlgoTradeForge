using AlgoTradeForge.HistoryLoader.Domain;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public class IntervalParserTests
{
    // ---------------------------------------------------------------------------
    // ToIntervalString
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(1,    "1m")]
    [InlineData(3,    "3m")]
    [InlineData(5,    "5m")]
    [InlineData(15,   "15m")]
    [InlineData(30,   "30m")]
    [InlineData(60,   "1h")]
    [InlineData(120,  "2h")]
    [InlineData(240,  "4h")]
    [InlineData(360,  "6h")]
    [InlineData(480,  "8h")]
    [InlineData(720,  "12h")]
    [InlineData(1440, "1d")]
    public void ToIntervalString_SupportedInterval_ReturnsExpectedString(int minutes, string expected)
    {
        var interval = TimeSpan.FromMinutes(minutes);
        Assert.Equal(expected, IntervalParser.ToIntervalString(interval));
    }

    [Fact]
    public void ToIntervalString_UnsupportedInterval_ThrowsArgumentException()
    {
        var unsupported = TimeSpan.FromMinutes(7);
        Assert.Throws<ArgumentException>(() => IntervalParser.ToIntervalString(unsupported));
    }

    // ---------------------------------------------------------------------------
    // ToTimeSpan
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData("1m",  1)]
    [InlineData("3m",  3)]
    [InlineData("5m",  5)]
    [InlineData("15m", 15)]
    [InlineData("30m", 30)]
    [InlineData("1h",  60)]
    [InlineData("2h",  120)]
    [InlineData("4h",  240)]
    [InlineData("6h",  360)]
    [InlineData("8h",  480)]
    [InlineData("12h", 720)]
    [InlineData("1d",  1440)]
    public void ToTimeSpan_SupportedString_ReturnsExpectedTimeSpan(string intervalStr, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), IntervalParser.ToTimeSpan(intervalStr));
    }

    [Fact]
    public void ToTimeSpan_UnsupportedString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => IntervalParser.ToTimeSpan("7m"));
    }

    // ---------------------------------------------------------------------------
    // Round-trip
    // ---------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    [InlineData(360)]
    [InlineData(480)]
    [InlineData(720)]
    [InlineData(1440)]
    public void RoundTrip_ToIntervalStringThenToTimeSpan_ReturnsSameValue(int minutes)
    {
        var original = TimeSpan.FromMinutes(minutes);
        var roundTripped = IntervalParser.ToTimeSpan(IntervalParser.ToIntervalString(original));
        Assert.Equal(original, roundTripped);
    }
}
