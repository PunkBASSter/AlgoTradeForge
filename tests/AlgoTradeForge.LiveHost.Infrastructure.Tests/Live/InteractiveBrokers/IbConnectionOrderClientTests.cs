using AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;
using Xunit;

namespace AlgoTradeForge.LiveHost.Infrastructure.Tests.Live.InteractiveBrokers;

public sealed class IbConnectionOrderClientTests
{
    // IBApi.Order seeds LmtPrice/AuxPrice to double.MaxValue in its ctor; an unset price must stay at that default.
    private const double UnsetPrice = double.MaxValue;

    [Fact]
    public void BuildIbOrder_Market_SetsTifDay_NoPrices()
    {
        var request = new IbOrderRequest("DU111", "BUY", "MKT", 3m, LmtPrice: null, AuxPrice: null);

        var order = IbConnectionOrderClient.BuildIbOrder(request);

        Assert.Equal("DAY", order.Tif);
        Assert.Equal("BUY", order.Action);
        Assert.Equal("MKT", order.OrderType);
        Assert.Equal(3m, order.TotalQuantity);
        Assert.Equal("DU111", order.Account);
        Assert.Equal(UnsetPrice, order.LmtPrice);
        Assert.Equal(UnsetPrice, order.AuxPrice);
    }

    [Fact]
    public void BuildIbOrder_Limit_SetsLmtPrice_LeavesAuxDefault()
    {
        var request = new IbOrderRequest("DU111", "SELL", "LMT", 2m, LmtPrice: 123.45, AuxPrice: null);

        var order = IbConnectionOrderClient.BuildIbOrder(request);

        Assert.Equal("DAY", order.Tif);
        Assert.Equal("LMT", order.OrderType);
        Assert.Equal(123.45, order.LmtPrice);
        Assert.Equal(UnsetPrice, order.AuxPrice);
    }

    [Fact]
    public void BuildIbOrder_Stop_SetsAuxPrice_LeavesLmtDefault()
    {
        var request = new IbOrderRequest("DU111", "SELL", "STP", 1m, LmtPrice: null, AuxPrice: 99.5);

        var order = IbConnectionOrderClient.BuildIbOrder(request);

        Assert.Equal("DAY", order.Tif);
        Assert.Equal("STP", order.OrderType);
        Assert.Equal(99.5, order.AuxPrice);
        Assert.Equal(UnsetPrice, order.LmtPrice);
    }

    [Theory]
    [InlineData("MKT", null, null)]
    [InlineData("LMT", 100.0, null)]
    [InlineData("STP", null, 50.0)]
    public void BuildIbOrder_AlwaysSetsTifDay(string orderType, double? lmt, double? aux)
    {
        var request = new IbOrderRequest("DU111", "BUY", orderType, 1m, lmt, aux);

        var order = IbConnectionOrderClient.BuildIbOrder(request);

        Assert.Equal("DAY", order.Tif);
    }
}
