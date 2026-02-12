using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.History;

public class IntBarTests
{
    [Fact]
    public void IntBar_CanHoldValuesExceedingIntMaxValue()
    {
        var largeValue = (long)int.MaxValue + 1;
        var bar = new Int64Bar(largeValue, largeValue + 100, largeValue - 100, largeValue + 50, largeValue * 10);

        Assert.Equal(largeValue, bar.Open);
        Assert.Equal(largeValue + 100, bar.High);
        Assert.Equal(largeValue - 100, bar.Low);
        Assert.Equal(largeValue + 50, bar.Close);
        Assert.Equal(largeValue * 10, bar.Volume);
    }

    [Fact]
    public void IntBar_RecordStructEquality_Works()
    {
        var bar1 = new Int64Bar(100, 200, 50, 150, 1000);
        var bar2 = new Int64Bar(100, 200, 50, 150, 1000);
        var bar3 = new Int64Bar(100, 200, 50, 151, 1000);

        Assert.Equal(bar1, bar2);
        Assert.NotEqual(bar1, bar3);
    }

    [Fact]
    public void IntBar_VolumeIsLong()
    {
        var bar = new Int64Bar(0, 0, 0, 0, long.MaxValue);
        Assert.Equal(long.MaxValue, bar.Volume);
    }

    [Fact]
    public void IntBar_AllFieldsAreLong()
    {
        var bar = new Int64Bar(
            Open: long.MaxValue,
            High: long.MaxValue,
            Low: long.MinValue,
            Close: long.MaxValue,
            Volume: long.MaxValue);

        Assert.Equal(long.MaxValue, bar.Open);
        Assert.Equal(long.MaxValue, bar.High);
        Assert.Equal(long.MinValue, bar.Low);
        Assert.Equal(long.MaxValue, bar.Close);
        Assert.Equal(long.MaxValue, bar.Volume);
    }

    [Fact]
    public void IntBar_BtcPrice_WithDecimalDigits8_FitsInLong()
    {
        // BTC at $100,000 with DecimalDigits=8: 100000 * 10^8 = 10_000_000_000_000
        var btcPrice = 10_000_000_000_000L;
        var bar = new Int64Bar(btcPrice, btcPrice + 1000, btcPrice - 1000, btcPrice, 500_000_000L);

        Assert.Equal(btcPrice, bar.Open);
        Assert.True(btcPrice < long.MaxValue);
    }
}
