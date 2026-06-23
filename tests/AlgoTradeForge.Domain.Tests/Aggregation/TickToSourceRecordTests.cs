using AlgoTradeForge.Domain.Aggregation;
using AlgoTradeForge.Domain.History;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Aggregation;

public class TickToSourceRecordTests
{
    [Fact]
    public void Buy_tick_sets_buy_volume_and_count_only()
    {
        var r = TickToSourceRecord.From(new TradeTick(123, 100, 5, 9, AggressorSide.Buy));
        Assert.Equal(123, r.TsMs);
        Assert.Equal(100, r.Open);
        Assert.Equal(100, r.High);
        Assert.Equal(100, r.Low);
        Assert.Equal(100, r.Close);
        Assert.Equal(5, r.Volume);
        Assert.Equal(5, r.BuyVolumeLong);
        Assert.Equal(1, r.BuyTradeCountLong);
        Assert.Equal(0, r.SellVolumeLong);
        Assert.Equal(0, r.SellTradeCountLong);
    }

    [Fact]
    public void Sell_tick_sets_sell_volume_and_count_only()
    {
        var r = TickToSourceRecord.From(new TradeTick(1, 200, 7, 1, AggressorSide.Sell));
        Assert.Equal(7, r.SellVolumeLong);
        Assert.Equal(1, r.SellTradeCountLong);
        Assert.Equal(0, r.BuyVolumeLong);
        Assert.Equal(0, r.BuyTradeCountLong);
    }

    [Fact]
    public void Unknown_aggressor_leaves_directional_fields_zero()
    {
        var r = TickToSourceRecord.From(new TradeTick(1, 200, 7, 1, AggressorSide.Unknown));
        Assert.Equal(0, r.BuyVolumeLong);
        Assert.Equal(0, r.SellVolumeLong);
    }
}
