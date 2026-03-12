using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Trading;

public class CashAndCarrySettlementTests
{
    private readonly CashAndCarrySettlement _settlement = CashAndCarrySettlement.Instance;

    #region ComputeCashDelta

    [Fact]
    public void ComputeCashDelta_Buy_DeductsFullNotional()
    {
        var fill = TestFills.BuyAapl(150L, 100m);

        var delta = _settlement.ComputeCashDelta(fill, fillRealizedPnl: 0L);

        // -(150 * 100 * 1) - 0 = -15000
        Assert.Equal(-15_000L, delta);
    }

    [Fact]
    public void ComputeCashDelta_Sell_AddsFullNotional()
    {
        var fill = TestFills.SellAapl(160L, 100m);

        var delta = _settlement.ComputeCashDelta(fill, fillRealizedPnl: 1000L);

        // +(160 * 100 * 1) - 0 = 16000
        Assert.Equal(16_000L, delta);
    }

    [Fact]
    public void ComputeCashDelta_WithCommission_DeductsCommission()
    {
        var fill = TestFills.BuyAapl(150L, 100m, commission: 10L);

        var delta = _settlement.ComputeCashDelta(fill, fillRealizedPnl: 0L);

        // -(150 * 100 * 1) - 10 = -15010
        Assert.Equal(-15_010L, delta);
    }

    [Fact]
    public void ComputeCashDelta_IgnoresRealizedPnl()
    {
        // CashAndCarry doesn't use fillRealizedPnl — cash flow is purely from notional exchange
        var fill = TestFills.SellAapl(160L, 50m);

        var withPnl = _settlement.ComputeCashDelta(fill, fillRealizedPnl: 500L);
        var withoutPnl = _settlement.ComputeCashDelta(fill, fillRealizedPnl: 0L);

        Assert.Equal(withPnl, withoutPnl);
    }

    [Fact]
    public void ComputeCashDelta_WithMultiplier_AppliesMultiplier()
    {
        // ES mini: multiplier = 50
        var fill = TestFills.BuyEs(5000L, 2m);

        var delta = _settlement.ComputeCashDelta(fill, fillRealizedPnl: 0L);

        // -(5000 * 2 * 50) - 0 = -500000
        Assert.Equal(-500_000L, delta);
    }

    #endregion

    #region ComputePositionValue

    [Fact]
    public void ComputePositionValue_LongPosition_ReturnsNotionalValue()
    {
        var position = new Position(TestAssets.Aapl, quantity: 100m, averageEntryPrice: 150L);

        var value = _settlement.ComputePositionValue(position, currentPrice: 160L);

        // 100 * 160 * 1 = 16000
        Assert.Equal(16_000L, value);
    }

    [Fact]
    public void ComputePositionValue_ShortPosition_ReturnsNegativeNotional()
    {
        var position = new Position(TestAssets.Aapl, quantity: -50m, averageEntryPrice: 150L);

        var value = _settlement.ComputePositionValue(position, currentPrice: 160L);

        // -50 * 160 * 1 = -8000
        Assert.Equal(-8_000L, value);
    }

    [Fact]
    public void ComputePositionValue_FlatPosition_ReturnsZero()
    {
        var position = new Position(TestAssets.Aapl);

        var value = _settlement.ComputePositionValue(position, currentPrice: 160L);

        Assert.Equal(0L, value);
    }

    #endregion

    #region ValidateSettlement

    [Fact]
    public void ValidateSettlement_Buy_SufficientCash_ReturnsNull()
    {
        var asset = TestAssets.Aapl;
        var order = CreateOrder(asset, OrderSide.Buy, 10m);
        var portfolio = CreatePortfolio(100_000L);

        var result = _settlement.ValidateSettlement(order, 150L, portfolio, commission: 0L);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSettlement_Buy_InsufficientCash_ReturnsError()
    {
        var asset = TestAssets.Aapl;
        var order = CreateOrder(asset, OrderSide.Buy, 1000m);
        var portfolio = CreatePortfolio(100_000L);

        var result = _settlement.ValidateSettlement(order, 150L, portfolio, commission: 0L);

        Assert.Equal("Insufficient cash", result);
    }

    [Fact]
    public void ValidateSettlement_SellClosingLong_NoMarginNeeded()
    {
        var asset = TestAssets.Aapl;
        var portfolio = CreatePortfolio(100_000L);
        portfolio.Apply(TestFills.BuyAapl(150L, 100m));
        var order = CreateOrder(asset, OrderSide.Sell, 50m);

        var result = _settlement.ValidateSettlement(order, 150L, portfolio, commission: 0L);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSettlement_SellOpeningShort_InsufficientMargin_ReturnsError()
    {
        var asset = TestAssets.Aapl;
        var order = CreateOrder(asset, OrderSide.Sell, 1000m);
        var portfolio = CreatePortfolio(100_000L);

        var result = _settlement.ValidateSettlement(order, 150L, portfolio, commission: 0L);

        Assert.Equal("Insufficient margin for short", result);
    }

    #endregion

    private static Order CreateOrder(Asset asset, OrderSide side, decimal quantity) =>
        new() { Id = 1, Asset = asset, Side = side, Type = OrderType.Market, Quantity = quantity };

    private static Portfolio CreatePortfolio(long initialCash)
    {
        var portfolio = new Portfolio { InitialCash = initialCash };
        portfolio.Initialize();
        return portfolio;
    }
}
