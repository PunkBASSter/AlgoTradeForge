using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.LiveHost.Application.Live.DataPlane;
using Xunit;

namespace AlgoTradeForge.LiveHost.Application.Tests.DataPlane;

public class BarSpecKeyTests
{
    [Fact]
    public void TimeBar_keys_with_same_timeframe_are_equal()
    {
        var tf = TimeFrame.Parse("1m");

        Assert.Equal(BarSpecKey.TimeBar(tf), BarSpecKey.TimeBar(tf));
    }

    [Fact]
    public void TimeBar_key_differs_from_AltBar_key()
    {
        var tf = TimeFrame.Parse("1m");

        Assert.NotEqual(BarSpecKey.TimeBar(tf), BarSpecKey.AltBar("EqV_1000"));
    }

    [Fact]
    public void TimeBar_key_differs_from_RawTick()
    {
        var tf = TimeFrame.Parse("1m");

        Assert.NotEqual(BarSpecKey.TimeBar(tf), BarSpecKey.RawTick);
    }

    [Fact]
    public void TimeBar_keys_with_different_timeframes_differ()
    {
        Assert.NotEqual(BarSpecKey.TimeBar(TimeFrame.Parse("1m")), BarSpecKey.TimeBar(TimeFrame.Parse("1h")));
    }
}
