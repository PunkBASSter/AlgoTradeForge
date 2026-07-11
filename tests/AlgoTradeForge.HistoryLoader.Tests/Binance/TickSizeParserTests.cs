using AlgoTradeForge.HistoryLoader.Infrastructure.Binance;
using Xunit;

namespace AlgoTradeForge.HistoryLoader.Tests.Binance;

public sealed class TickSizeParserTests
{
    [Theory]
    [InlineData("0.01000000", 2)]
    [InlineData("0.10", 1)]
    [InlineData("1", 0)]
    [InlineData("0.00001", 5)]
    [InlineData("100", 0)]
    public void FractionalDigits_IgnoresTrailingZeros(string tickSize, int expected) =>
        Assert.Equal(expected, TickSizeParser.FractionalDigits(tickSize));
}
