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
}
