using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class BarMatcherTests
{
    private readonly BarMatcher _matcher = new();

    private static BacktestOptions CreateOptions(Asset asset, decimal commission = 0m, long slippageTicks = 0) =>
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
    public void GetFillPrice_MarketBuy_FillsAtOpen()
    {
        var order = TestOrders.MarketBuy(TestAssets.Aapl, 100m);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(15000m, price);
    }

    [Fact]
    public void GetFillPrice_MarketSell_FillsAtOpen()
    {
        var order = TestOrders.MarketSell(TestAssets.Aapl, 100m);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(15000m, price);
    }

    [Fact]
    public void GetFillPrice_LimitBuyPriceAtOrAboveLow_Fills()
    {
        var order = TestOrders.LimitBuy(TestAssets.Aapl, 100m, limitPrice: 14800L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(14800m, price);
    }

    [Fact]
    public void GetFillPrice_LimitBuyPriceBelowLow_Rejects()
    {
        var order = TestOrders.LimitBuy(TestAssets.Aapl, 100m, limitPrice: 14700L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Null(price);
    }

    [Fact]
    public void GetFillPrice_LimitSellPriceAtOrBelowHigh_Fills()
    {
        var order = TestOrders.LimitSell(TestAssets.Aapl, 100m, limitPrice: 15500L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(15500m, price);
    }

    [Fact]
    public void GetFillPrice_LimitSellPriceAboveHigh_Rejects()
    {
        var order = TestOrders.LimitSell(TestAssets.Aapl, 100m, limitPrice: 15600L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Null(price);
    }

    #region Stop Order Tests (T023)

    [Fact]
    public void GetFillPrice_StopBuy_TriggersWhenHighReachesStopPrice()
    {
        // Stop Buy triggers when price rises to stop price (breakout entry)
        var order = TestOrders.StopBuy(TestAssets.Aapl, 100m, stopPrice: 15200L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High=15500 >= Stop=15200
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(15200m, price);
    }

    [Fact]
    public void GetFillPrice_StopBuy_DoesNotTriggerWhenHighBelowStop()
    {
        var order = TestOrders.StopBuy(TestAssets.Aapl, 100m, stopPrice: 16000L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High=15500 < Stop=16000
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Null(price);
    }

    [Fact]
    public void GetFillPrice_StopBuy_GapsUp_FillsAtOpen()
    {
        // When bar opens above stop price, fill at open (gap up)
        var order = TestOrders.StopBuy(TestAssets.Aapl, 100m, stopPrice: 15000L);
        var bar = TestBars.Create(15500, 16000, 15400, 15800); // Open=15500 > Stop=15000
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(15500m, price); // Fill at open (slippage through gap)
    }

    [Fact]
    public void GetFillPrice_StopSell_TriggersWhenLowReachesStopPrice()
    {
        // Stop Sell triggers when price falls to stop price (breakdown entry)
        var order = TestOrders.StopSell(TestAssets.Aapl, 100m, stopPrice: 14900L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // Low=14800 <= Stop=14900
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(14900m, price);
    }

    [Fact]
    public void GetFillPrice_StopSell_DoesNotTriggerWhenLowAboveStop()
    {
        var order = TestOrders.StopSell(TestAssets.Aapl, 100m, stopPrice: 14000L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // Low=14800 > Stop=14000
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Null(price);
    }

    [Fact]
    public void GetFillPrice_StopSell_GapsDown_FillsAtOpen()
    {
        var order = TestOrders.StopSell(TestAssets.Aapl, 100m, stopPrice: 15500L);
        var bar = TestBars.Create(15000, 15200, 14800, 14900); // Open=15000 < Stop=15500
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(15000m, price); // Fill at open (gap down)
    }

    [Fact]
    public void GetFillPrice_StopBuy_WithSlippage_FillsAtStopPlusSlippage()
    {
        var order = TestOrders.StopBuy(TestAssets.Aapl, 100m, stopPrice: 15200L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl, slippageTicks: 5);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(15205m, price);
    }

    [Fact]
    public void GetFillPrice_StopSell_WithSlippage_FillsAtStopMinusSlippage()
    {
        var order = TestOrders.StopSell(TestAssets.Aapl, 100m, stopPrice: 14900L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl, slippageTicks: 5);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(14895m, price);
    }

    #endregion

    #region StopLimit Order Tests (T023)

    [Fact]
    public void GetFillPrice_StopLimitBuy_TriggersAndFillsWhenLimitInRange()
    {
        // StopLimit Buy: stop triggers at 15200, then limit buy at 15300 if price allows
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 15200L, limitPrice: 15300L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High >= Stop, Limit <= High
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(15300m, price); // Fill at limit price
    }

    [Fact]
    public void GetFillPrice_StopLimitBuy_TriggersButLimitNotReached_ReturnsNull()
    {
        // Stop triggers but limit price not reached — returns null (engine handles Triggered state)
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 15200L, limitPrice: 14700L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High >= Stop, but Low > Limit
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Null(price);
    }

    [Fact]
    public void GetFillPrice_StopLimitBuy_AlreadyTriggered_FillsAsLimit()
    {
        // Once triggered, StopLimit acts as a regular Limit order
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 15200L, limitPrice: 14900L);
        order.Status = OrderStatus.Triggered;
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // Low=14800 <= Limit=14900
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(14900m, price);
    }

    [Fact]
    public void GetFillPrice_StopLimitSell_TriggersAndFillsWhenLimitInRange()
    {
        var order = TestOrders.StopLimitSell(TestAssets.Aapl, 100m, stopPrice: 14900L, limitPrice: 14800L);
        var bar = TestBars.Create(15000, 15500, 14700, 15200); // Low <= Stop, Limit >= Low
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(14800m, price);
    }

    [Fact]
    public void GetFillPrice_StopLimitSell_TriggersButLimitNotReached_ReturnsNull()
    {
        var order = TestOrders.StopLimitSell(TestAssets.Aapl, 100m, stopPrice: 14900L, limitPrice: 15600L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // Low <= Stop, but High < Limit
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Null(price);
    }

    [Fact]
    public void GetFillPrice_StopLimitBuy_StopNotReached_ReturnsNull()
    {
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 16000L, limitPrice: 16100L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200); // High < Stop
        var options = CreateOptions(TestAssets.Aapl);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Null(price);
    }

    [Fact]
    public void GetFillPrice_StopLimitBuy_NoSlippageOnLimitFill()
    {
        // StopLimit orders fill at limit price — no slippage
        var order = TestOrders.StopLimitBuy(TestAssets.Aapl, 100m, stopPrice: 15200L, limitPrice: 15300L);
        var bar = TestBars.Create(15000, 15500, 14800, 15200);
        var options = CreateOptions(TestAssets.Aapl, slippageTicks: 10);

        var price = _matcher.GetFillPrice(order, bar, options);

        Assert.Equal(15300m, price); // Limit price, no slippage
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
    public void EvaluateSlTp_BuySide_SlOnlyHit_ReturnsSlResult()
    {
        var order = BuyOrderWithSlTp(slPrice: 14500L, new TakeProfitLevel(16000L, 1m));
        // Bar drops to SL but doesn't reach TP
        var bar = TestBars.Create(15000, 15200, 14400, 14800); // Low=14400 <= SL=14500, High=15200 < TP=16000
        var options = CreateOptions(TestAssets.Aapl);

        var result = _matcher.EvaluateSlTp(order, 15000m, 0, bar, options);

        Assert.NotNull(result);
        Assert.Equal(14500m, result.Value.Price);
        Assert.True(result.Value.IsStopLoss);
        Assert.Equal(1m, result.Value.ClosurePercentage);
    }

    [Fact]
    public void EvaluateSlTp_BuySide_TpOnlyHit_ReturnsTpResult()
    {
        var order = BuyOrderWithSlTp(slPrice: 14500L, new TakeProfitLevel(15800L, 1m));
        // Bar rises to TP but doesn't drop to SL
        var bar = TestBars.Create(15000, 16000, 14600, 15500); // High=16000 >= TP=15800, Low=14600 > SL=14500
        var options = CreateOptions(TestAssets.Aapl);

        var result = _matcher.EvaluateSlTp(order, 15000m, 0, bar, options);

        Assert.NotNull(result);
        Assert.Equal(15800m, result.Value.Price);
        Assert.False(result.Value.IsStopLoss);
        Assert.Equal(0, result.Value.TpIndex);
        Assert.Equal(1m, result.Value.ClosurePercentage);
    }

    [Fact]
    public void EvaluateSlTp_BuySide_BothInRange_WorstCase_ReturnsSl()
    {
        var order = BuyOrderWithSlTp(slPrice: 14500L, new TakeProfitLevel(15800L, 1m));
        // Bar covers both SL and TP
        var bar = TestBars.Create(15000, 16000, 14000, 15500); // Low=14000 <= SL, High=16000 >= TP
        var options = CreateOptions(TestAssets.Aapl);

        var result = _matcher.EvaluateSlTp(order, 15000m, 0, bar, options);

        Assert.NotNull(result);
        Assert.Equal(14500m, result.Value.Price); // Worst case: SL wins
        Assert.True(result.Value.IsStopLoss);
    }

    [Fact]
    public void EvaluateSlTp_BuySide_NeitherHit_ReturnsNull()
    {
        var order = BuyOrderWithSlTp(slPrice: 14000L, new TakeProfitLevel(16000L, 1m));
        var bar = TestBars.Create(15000, 15200, 14100, 15100); // Low=14100 > SL=14000, High=15200 < TP=16000
        var options = CreateOptions(TestAssets.Aapl);

        var result = _matcher.EvaluateSlTp(order, 15000m, 0, bar, options);

        Assert.Null(result);
    }

    [Fact]
    public void EvaluateSlTp_SellSide_SlOnlyHit_ReturnsSlResult()
    {
        var order = SellOrderWithSlTp(slPrice: 15500L, new TakeProfitLevel(14000L, 1m));
        // Short position: SL is above entry, bar rises to SL
        var bar = TestBars.Create(15000, 15600, 14200, 15300); // High=15600 >= SL=15500, Low=14200 > TP=14000
        var options = CreateOptions(TestAssets.Aapl);

        var result = _matcher.EvaluateSlTp(order, 15000m, 0, bar, options);

        Assert.NotNull(result);
        Assert.Equal(15500m, result.Value.Price);
        Assert.True(result.Value.IsStopLoss);
    }

    [Fact]
    public void EvaluateSlTp_SellSide_TpOnlyHit_ReturnsTpResult()
    {
        var order = SellOrderWithSlTp(slPrice: 15500L, new TakeProfitLevel(14200L, 1m));
        // Short position: TP is below entry, bar drops to TP
        var bar = TestBars.Create(15000, 15400, 14100, 14500); // Low=14100 <= TP=14200, High=15400 < SL=15500
        var options = CreateOptions(TestAssets.Aapl);

        var result = _matcher.EvaluateSlTp(order, 15000m, 0, bar, options);

        Assert.NotNull(result);
        Assert.Equal(14200m, result.Value.Price);
        Assert.False(result.Value.IsStopLoss);
    }

    [Fact]
    public void EvaluateSlTp_ZeroRangeBar_NoSlTpHit()
    {
        var order = BuyOrderWithSlTp(slPrice: 14000L, new TakeProfitLevel(16000L, 1m));
        var bar = TestBars.Create(15000, 15000, 15000, 15000); // Zero-range bar
        var options = CreateOptions(TestAssets.Aapl);

        var result = _matcher.EvaluateSlTp(order, 15000m, 0, bar, options);

        Assert.Null(result);
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

        var result1 = _matcher.EvaluateSlTp(order, 15000m, 0, bar1, options);

        Assert.NotNull(result1);
        Assert.Equal(15500m, result1.Value.Price);
        Assert.Equal(0.5m, result1.Value.ClosurePercentage);
        Assert.False(result1.Value.IsStopLoss);
        Assert.Equal(0, result1.Value.TpIndex);

        // Second TP hit
        var bar2 = TestBars.Create(15800, 16200, 15700, 16000);
        var result2 = _matcher.EvaluateSlTp(order, 15000m, 1, bar2, options);

        Assert.NotNull(result2);
        Assert.Equal(16000m, result2.Value.Price);
        Assert.Equal(1m, result2.Value.ClosurePercentage);
        Assert.False(result2.Value.IsStopLoss);
        Assert.Equal(1, result2.Value.TpIndex);
    }

    #endregion
}
