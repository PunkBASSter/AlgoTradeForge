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
        var order = TestOrders.LimitBuy(TestAssets.Aapl, 100m, limitPrice: 14800L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(14800m, fill.Price);
    }

    [Fact]
    public void TryFill_LimitBuyPriceBelowLow_Rejects()
    {
        var order = TestOrders.LimitBuy(TestAssets.Aapl, 100m, limitPrice: 14700L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
    }

    [Fact]
    public void TryFill_LimitSellPriceAtOrBelowHigh_Fills()
    {
        var order = TestOrders.LimitSell(TestAssets.Aapl, 100m, limitPrice: 15500L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15500m, fill.Price);
    }

    [Fact]
    public void TryFill_LimitSellPriceAboveHigh_Rejects()
    {
        var order = TestOrders.LimitSell(TestAssets.Aapl, 100m, limitPrice: 15600L);
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

    #region Stop Order Tests (T023)

    [Fact]
    public void TryFill_StopBuy_TriggersWhenHighReachesStopPrice()
    {
        // Stop Buy triggers when price rises to stop price (breakout entry)
        var order = TestOrders.StopBuy(TestAssets.Aapl, 100m, stopPrice: 15200L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High=15500 >= Stop=15200
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15200m, fill.Price);
    }

    [Fact]
    public void TryFill_StopBuy_DoesNotTriggerWhenHighBelowStop()
    {
        var order = TestOrders.StopBuy(TestAssets.Aapl, 100m, stopPrice: 16000L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High=15500 < Stop=16000
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
    }

    [Fact]
    public void TryFill_StopBuy_GapsUp_FillsAtOpen()
    {
        // When bar opens above stop price, fill at open (gap up)
        var order = TestOrders.StopBuy(TestAssets.Aapl, 100m, stopPrice: 15000L);
        var bar = TestBars.Create(15500, 16000, 15400, 15800); // Open=15500 > Stop=15000
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15500m, fill.Price); // Fill at open (slippage through gap)
    }

    [Fact]
    public void TryFill_StopSell_TriggersWhenLowReachesStopPrice()
    {
        // Stop Sell triggers when price falls to stop price (breakdown entry)
        var order = TestOrders.StopSell(TestAssets.Aapl, 100m, stopPrice: 14900L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // Low=14800 <= Stop=14900
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(14900m, fill.Price);
    }

    [Fact]
    public void TryFill_StopSell_DoesNotTriggerWhenLowAboveStop()
    {
        var order = TestOrders.StopSell(TestAssets.Aapl, 100m, stopPrice: 14000L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // Low=14800 > Stop=14000
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
    }

    [Fact]
    public void TryFill_StopSell_GapsDown_FillsAtOpen()
    {
        var order = TestOrders.StopSell(TestAssets.Aapl, 100m, stopPrice: 15500L);
        var bar = TestBars.Create(15000, 15200, 14800, 14900); // Open=15000 < Stop=15500
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15000m, fill.Price); // Fill at open (gap down)
    }

    [Fact]
    public void TryFill_StopBuy_WithSlippage_FillsAtStopPlusSlippage()
    {
        var order = TestOrders.StopBuy(TestAssets.Aapl, 100m, stopPrice: 15200L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl, slippageTicks: 5m); // 5 * 0.01 = 0.05

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15200m + 0.05m, fill.Price);
    }

    [Fact]
    public void TryFill_StopSell_WithSlippage_FillsAtStopMinusSlippage()
    {
        var order = TestOrders.StopSell(TestAssets.Aapl, 100m, stopPrice: 14900L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl, slippageTicks: 5m);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(14900m - 0.05m, fill.Price);
    }

    #endregion

    #region StopLimit Order Tests (T023)

    [Fact]
    public void TryFill_StopLimitBuy_TriggersAndFillsWhenLimitInRange()
    {
        // StopLimit Buy: stop triggers at 15200, then limit buy at 15200 if price allows
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 15200L, limitPrice: 15300L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High >= Stop, Limit <= High
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15300L, fill.Price); // Fill at limit price
    }

    [Fact]
    public void TryFill_StopLimitBuy_TriggersButLimitNotReached_Pending()
    {
        // Stop triggers but limit price not reached — order becomes Triggered (pending limit)
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 15200L, limitPrice: 14700L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High >= Stop, but Low > Limit
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
        Assert.Equal(OrderStatus.Triggered, order.Status);
    }

    [Fact]
    public void TryFill_StopLimitBuy_AlreadyTriggered_FillsAsLimit()
    {
        // Once triggered, StopLimit acts as a regular Limit order
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 15200L, limitPrice: 14900L);
        order.Status = OrderStatus.Triggered;
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // Low=14800 <= Limit=14900
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(14900m, fill.Price);
    }

    [Fact]
    public void TryFill_StopLimitSell_TriggersAndFillsWhenLimitInRange()
    {
        var order = TestOrders.StopLimitSell(TestAssets.Aapl, 100m, stopPrice: 14900L, limitPrice: 14800L);
        var bar = TestBars.Create(15000, 15500, 14700, 15200); // Low <= Stop, Limit >= Low
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(14800m, fill.Price);
    }

    [Fact]
    public void TryFill_StopLimitSell_TriggersButLimitNotReached_Pending()
    {
        var order = TestOrders.StopLimitSell(TestAssets.Aapl, 100m, stopPrice: 14900L, limitPrice: 15600L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // Low <= Stop, but High < Limit
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
        Assert.Equal(OrderStatus.Triggered, order.Status);
    }

    [Fact]
    public void TryFill_StopLimitBuy_StopNotReached_NothingHappens()
    {
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 16000L, limitPrice: 16100L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High < Stop
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.Null(fill);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    [Fact]
    public void TryFill_StopLimitBuy_NoSlippageOnLimitFill()
    {
        // StopLimit orders fill at limit price — no slippage
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 15200L, limitPrice: 15300L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl, slippageTicks: 10m);

        var fill = _matcher.TryFill(order, bar, options);

        Assert.NotNull(fill);
        Assert.Equal(15300m, fill.Price); // Limit price, no slippage
    }

    #endregion

    #region SL/TP Evaluation Tests (T030)

    private static Order BuyOrderWithSlTp(long slPrice, params TakeProfitLevel[] tpLevels) =>
        new()
        {
            Id = 1,
            Asset = TestAssets.Aapl,
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Quantity = 100m,
            StopLossPrice = slPrice,
            TakeProfitLevels = tpLevels.Length > 0 ? tpLevels : null,
        };

    private static Order SellOrderWithSlTp(long slPrice, params TakeProfitLevel[] tpLevels) =>
        new()
        {
            Id = 2,
            Asset = TestAssets.Aapl,
            Side = OrderSide.Sell,
            Type = OrderType.Market,
            Quantity = 100m,
            StopLossPrice = slPrice,
            TakeProfitLevels = tpLevels.Length > 0 ? tpLevels : null,
        };

    [Fact]
    public void EvaluateSlTp_BuySide_SlOnlyHit_ReturnsSlFill()
    {
        var order = BuyOrderWithSlTp(slPrice: 14500L, new TakeProfitLevel(16000L, 1m));
        // Bar drops to SL but doesn't reach TP
        var bar = TestBars.Create(15000, 15200, 14400, 14800); // Low=14400 <= SL=14500, High=15200 < TP=16000
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.EvaluateSlTp(order, 15000m, 100m, 0, bar, options, out _);

        Assert.NotNull(fill);
        Assert.Equal(14500m, fill.Price);
        Assert.Equal(100m, fill.Quantity);
        Assert.Equal(OrderSide.Sell, fill.Side); // Close side for long
    }

    [Fact]
    public void EvaluateSlTp_BuySide_TpOnlyHit_ReturnsTpFill()
    {
        var order = BuyOrderWithSlTp(slPrice: 14500L, new TakeProfitLevel(15800L, 1m));
        // Bar rises to TP but doesn't drop to SL
        var bar = TestBars.Create(15000, 16000, 14600, 15500); // High=16000 >= TP=15800, Low=14600 > SL=14500
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.EvaluateSlTp(order, 15000m, 100m, 0, bar, options, out var hitTpIndex);

        Assert.NotNull(fill);
        Assert.Equal(15800m, fill.Price);
        Assert.Equal(100m, fill.Quantity);
        Assert.Equal(OrderSide.Sell, fill.Side);
        Assert.Equal(0, hitTpIndex);
    }

    [Fact]
    public void EvaluateSlTp_BuySide_BothInRange_WorstCase_ReturnsSl()
    {
        var order = BuyOrderWithSlTp(slPrice: 14500L, new TakeProfitLevel(15800L, 1m));
        // Bar covers both SL and TP
        var bar = TestBars.Create(15000, 16000, 14000, 15500); // Low=14000 <= SL, High=16000 >= TP
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.EvaluateSlTp(order, 15000m, 100m, 0, bar, options, out _);

        Assert.NotNull(fill);
        Assert.Equal(14500m, fill.Price); // Worst case: SL wins
    }

    [Fact]
    public void EvaluateSlTp_BuySide_NeitherHit_ReturnsNull()
    {
        var order = BuyOrderWithSlTp(slPrice: 14000L, new TakeProfitLevel(16000L, 1m));
        var bar = TestBars.Create(15000, 15200, 14100, 15100); // Low=14100 > SL=14000, High=15200 < TP=16000
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.EvaluateSlTp(order, 15000m, 100m, 0, bar, options, out _);

        Assert.Null(fill);
    }

    [Fact]
    public void EvaluateSlTp_SellSide_SlOnlyHit_ReturnsSlFill()
    {
        var order = SellOrderWithSlTp(slPrice: 15500L, new TakeProfitLevel(14000L, 1m));
        // Short position: SL is above entry, bar rises to SL
        var bar = TestBars.Create(15000, 15600, 14200, 15300); // High=15600 >= SL=15500, Low=14200 > TP=14000
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.EvaluateSlTp(order, 15000m, 100m, 0, bar, options, out _);

        Assert.NotNull(fill);
        Assert.Equal(15500m, fill.Price);
        Assert.Equal(OrderSide.Buy, fill.Side); // Close side for short
    }

    [Fact]
    public void EvaluateSlTp_SellSide_TpOnlyHit_ReturnsTpFill()
    {
        var order = SellOrderWithSlTp(slPrice: 15500L, new TakeProfitLevel(14200L, 1m));
        // Short position: TP is below entry, bar drops to TP
        var bar = TestBars.Create(15000, 15400, 14100, 14500); // Low=14100 <= TP=14200, High=15400 < SL=15500
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.EvaluateSlTp(order, 15000m, 100m, 0, bar, options, out _);

        Assert.NotNull(fill);
        Assert.Equal(14200m, fill.Price);
    }

    [Fact]
    public void EvaluateSlTp_ZeroRangeBar_NoSlTpHit()
    {
        var order = BuyOrderWithSlTp(slPrice: 14000L, new TakeProfitLevel(16000L, 1m));
        var bar = TestBars.Create(15000, 15000, 15000, 15000); // Zero-range bar
        var options = CreateOptions(TestAssets.Aapl);

        var fill = _matcher.EvaluateSlTp(order, 15000m, 100m, 0, bar, options, out _);

        Assert.Null(fill);
    }

    [Fact]
    public void EvaluateSlTp_MultipleTpLevels_PartialClosure()
    {
        var order = BuyOrderWithSlTp(
            slPrice: 14000L,
            new TakeProfitLevel(15500L, 0.5m),  // TP1: 50% at 15500
            new TakeProfitLevel(16000L, 1m));    // TP2: remaining at 16000

        // First TP hit
        var bar1 = TestBars.Create(15000, 15600, 14100, 15300); // High=15600 >= TP1=15500
        var options = CreateOptions(TestAssets.Aapl);

        var fill1 = _matcher.EvaluateSlTp(order, 15000m, 100m, 0, bar1, options, out var hitTpIndex1);

        Assert.NotNull(fill1);
        Assert.Equal(15500m, fill1.Price);
        Assert.Equal(50m, fill1.Quantity); // 50% of 100
        Assert.Equal(0, hitTpIndex1);

        // Second TP hit (remaining 50 shares)
        var bar2 = TestBars.Create(15800, 16200, 15700, 16000);
        var fill2 = _matcher.EvaluateSlTp(order, 15000m, 50m, 1, bar2, options, out var hitTpIndex2);

        Assert.NotNull(fill2);
        Assert.Equal(16000m, fill2.Price);
        Assert.Equal(50m, fill2.Quantity); // 100% of remaining 50
        Assert.Equal(1, hitTpIndex2);
    }

    #endregion
}
