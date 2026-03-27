using AlgoTradeForge.Domain.Engine;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class TimeFrameFormatterTests
{
    [Theory]
    [InlineData(30, "30s")]
    [InlineData(60, "1m")]
    [InlineData(300, "5m")]
    [InlineData(3600, "1h")]
    [InlineData(14400, "4h")]
    [InlineData(86400, "1d")]
    public void Format_ReturnsExpectedString(int seconds, string expected)
    {
        Assert.Equal(expected, TimeFrameFormatter.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("1m", 60)]
    [InlineData("5m", 300)]
    [InlineData("15m", 900)]
    [InlineData("1h", 3600)]
    [InlineData("4h", 14400)]
    [InlineData("1d", 86400)]
    public void TryParseShorthand_ValidInput_ReturnsExpectedTimeSpan(string input, int expectedSeconds)
    {
        Assert.True(TimeFrameFormatter.TryParseShorthand(input, out var result));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Theory]
    [InlineData("30s", 30)]
    [InlineData("1m", 60)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    [InlineData("4h", 14400)]
    [InlineData("1d", 86400)]
    public void Format_ThenTryParseShorthand_RoundTrips(string _, int seconds)
    {
        var original = TimeSpan.FromSeconds(seconds);
        var formatted = TimeFrameFormatter.Format(original);
        Assert.True(TimeFrameFormatter.TryParseShorthand(formatted, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("h")]
    [InlineData("abc")]
    [InlineData("01:00:00")]
    [InlineData("0m")]
    [InlineData("-1h")]
    public void TryParseShorthand_InvalidInput_ReturnsFalse(string? input)
    {
        Assert.False(TimeFrameFormatter.TryParseShorthand(input, out _));
    }
}
