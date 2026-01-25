using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class BarMatcherTests
{
    private readonly BarMatcher _matcher = new();

    private BacktestOptions CreateOptions(Asset asset, decimal commission = 0m, decimal slippageTicks = 0m) =>
        new()
        {
            InitialCash = 100_000m,
            Asset = asset,
            CommissionPerTrade = commission,
            SlippageTicks = slippageTicks
        };

    [Fact]
    public void TryFill_MarketBuy_FillsAtOpen()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var bar = TestBars.Create(150m, 155m, 148m, 152m);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(150m, fill.Price);
    }

    [Fact]
    public void TryFill_MarketSell_FillsAtOpen()
    {
        var order = TestOrders.MarketSell(TestAssets.Aapl, 100m);
        var bar = TestBars.Create(150m, 155m, 148m, 152m);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(150m, fill.Price);
    }

    [Theory]
    [InlineData(1, 150.01)]  // 1 tick slippage for equity
    [InlineData(2, 150.02)]  // 2 ticks slippage
    [InlineData(0, 150.00)]  // No slippage
    public void TryFill_MarketBuyWithSlippage_AddsTicks(decimal slippageTicks, decimal expectedPrice)
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var bar = TestBars.Create(150m, 155m, 148m, 152m);
        var options = CreateOptions(TestAssets.Aapl, slippageTicks: slippageTicks);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Equal(expectedPrice, fill!.Price);
    }

    [Theory]
    [InlineData(1, 149.99)]  // 1 tick slippage subtracted for sell
    [InlineData(2, 149.98)]
    public void TryFill_MarketSellWithSlippage_SubtractsTicks(decimal slippageTicks, decimal expectedPrice)
    {
        var order = TestOrders.MarketSell(TestAssets.Aapl, 100m);
        var bar = TestBars.Create(150m, 155m, 148m, 152m);
        var options = CreateOptions(TestAssets.Aapl, slippageTicks: slippageTicks);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Equal(expectedPrice, fill!.Price);
    }

    [Fact]
    public void TryFill_MarketBuyFutures_AppliesTickSize()
    {
        var order = TestOrders.MarketBuy(TestAssets.EsMini, 1m);
        var bar = TestBars.Create(5000m, 5020m, 4990m, 5010m);
        var options = CreateOptions(TestAssets.EsMini, slippageTicks: 2m);

        var fill = _matcher.TryFill(order, bar, options);

        // 5000 + 2 * 0.25 = 5000.50
        Assert.Equal(5000.50m, fill!.Price);
    }

    [Fact]
    public void TryFill_LimitBuyPriceAtOrAboveLow_Fills()
    {
        var order = TestOrders.LimitBuy(TestAssets.Aapl, 100m, limitPrice: 148m);
        var bar = TestBars.Create(150m, 155m, 148m, 152m);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(148m, fill.Price);
    }

    [Fact]
    public void TryFill_LimitBuyPriceBelowLow_Rejects()
    {
        var order = TestOrders.LimitBuy(TestAssets.Aapl, 100m, limitPrice: 147m);
        var bar = TestBars.Create(150m, 155m, 148m, 152m);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
    }

    [Fact]
    public void TryFill_LimitSellPriceAtOrBelowHigh_Fills()
    {
        var order = TestOrders.LimitSell(TestAssets.Aapl, 100m, limitPrice: 155m);
        var bar = TestBars.Create(150m, 155m, 148m, 152m);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(155m, fill.Price);
    }

    [Fact]
    public void TryFill_LimitSellPriceAboveHigh_Rejects()
    {
        var order = TestOrders.LimitSell(TestAssets.Aapl, 100m, limitPrice: 156m);
        var bar = TestBars.Create(150m, 155m, 148m, 152m);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
    }

    [Fact]
    public void TryFill_Fill_HasCorrectTimestamp()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var timestamp = new DateTimeOffset(2024, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var bar = TestBars.Create(150m, 155m, 148m, 152m, timestamp: timestamp);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Equal(timestamp, fill!.Timestamp);
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
        var bar = TestBars.Create(5000m, 5020m, 4990m, 5010m);
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
