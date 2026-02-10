using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class BarMatcherTests
{
    private readonly BarMatcher _matcher = new();

    private static BacktestOptions CreateOptions(Asset asset, decimal commission = 0m, decimal slippageTicks = 0m) =>
        new()
        {
            InitialCash = 100_000m,
            Asset = asset,
            StartTime = DateTimeOffset.MinValue,
            EndTime = DateTimeOffset.MaxValue,
            CommissionPerTrade = commission,
            SlippageTicks = slippageTicks
        };

    [Fact]
    public void TryFill_MarketBuy_FillsAtOpen()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15000m, fill.Price);
    }

    [Fact]
    public void TryFill_MarketSell_FillsAtOpen()
    {
        var order = TestOrders.MarketSell(TestAssets.Aapl, 100m);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15000m, fill.Price);
    }

    [Fact]
    public void TryFill_LimitBuyPriceAtOrAboveLow_Fills()
    {
        var order = TestOrders.LimitBuy(TestAssets.Aapl, 100m, limitPrice: 14800m);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(14800m, fill.Price);
    }

    [Fact]
    public void TryFill_LimitBuyPriceBelowLow_Rejects()
    {
        var order = TestOrders.LimitBuy(TestAssets.Aapl, 100m, limitPrice: 14700m);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
    }

    [Fact]
    public void TryFill_LimitSellPriceAtOrBelowHigh_Fills()
    {
        var order = TestOrders.LimitSell(TestAssets.Aapl, 100m, limitPrice: 15500m);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15500m, fill.Price);
    }

    [Fact]
    public void TryFill_LimitSellPriceAboveHigh_Rejects()
    {
        var order = TestOrders.LimitSell(TestAssets.Aapl, 100m, limitPrice: 15600m);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
    }

    [Fact]
    public void TryFill_Fill_HasCorrectOrderId()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var bar = TestBars.Bullish();
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Equal(order.Id, fill!.OrderId);
    }

    [Fact]
    public void TryFill_Fill_HasCorrectAsset()
    {
        var order = TestOrders.MarketBuy(TestAssets.EsMini, 1m);
        var bar = TestBars.Create(500000, 502000, 499000, 501000);
        var options = CreateOptions(TestAssets.EsMini);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Equal(TestAssets.EsMini, fill!.Asset);
    }

    [Fact]
    public void TryFill_Fill_HasCorrectCommission()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var bar = TestBars.Bullish();
        var options = CreateOptions(TestAssets.Aapl, commission: 2.50m);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Equal(2.50m, fill!.Commission);
    }

    [Fact]
    public void TryFill_Fill_HasCorrectQuantity()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 75m);
        var bar = TestBars.Bullish();
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Equal(75m, fill!.Quantity);
    }

    [Fact]
    public void TryFill_Fill_HasCorrectSide()
    {
        var buyOrder = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var sellOrder = TestOrders.MarketSell(TestAssets.Aapl, 100m);
        var bar = TestBars.Bullish();
        var options = CreateOptions(TestAssets.Aapl);

        var buyFill = _matcher.TryFill(buyOrder, bar, options);
        var sellFill = _matcher.TryFill(sellOrder, bar, options);

        Assert.Equal(OrderSide.Buy, buyFill!.Side);
        Assert.Equal(OrderSide.Sell, sellFill!.Side);
    }
}
