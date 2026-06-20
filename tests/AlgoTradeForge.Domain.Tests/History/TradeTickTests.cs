using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.History;

public class TradeTickTests
{
    [Fact]
    public void Timestamp_ConvertsUnixMillisToDateTimeOffset()
    {
        var tick = new TradeTick(1_700_000_000_000, 5_000_000, 1_250, 42, AggressorSide.Buy);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), tick.Timestamp);
        Assert.Equal(AggressorSide.Buy, tick.Aggressor);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = new TradeTick(1, 2, 3, 4, AggressorSide.Sell);
        var b = new TradeTick(1, 2, 3, 4, AggressorSide.Sell);

        Assert.Equal(a, b);
    }
}
