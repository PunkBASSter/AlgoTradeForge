using AlgoTradeForge.Domain.Tests.TestUtilities;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Trading;

public class MarginSettlementTests
{
    private readonly MarginSettlement _settlement = MarginSettlement.Instance;

    private static readonly IReadOnlyDictionary<string, long> NoPrices =
        new Dictionary<string, long>();

    private static FutureAsset TestPerp => new() { Name = "BTCUSDT_PERP", Exchange = "Binance",
        Multiplier = 1m, TickSize = 0.01m, MarginRequirement = 0.10m };

    #region ComputeCashDelta

    [Fact]
    public void ComputeCashDelta_OpenPosition_DeductsOnlyCommission()
    {
        var fill = TestFills.Buy(TestPerp, 50_000L, 1m, commission: 25L);

        // Opening a position: fillRealizedPnl = 0
        var delta = _settlement.ComputeCashDelta(fill, fillRealizedPnl: 0L);

        // 0 - 25 = -25 (only commission)
        Assert.Equal(-25L, delta);
    }

    [Fact]
    public void ComputeCashDelta_CloseWithProfit_AddsPnlMinusCommission()
    {
        var fill = TestFills.Sell(TestPerp, 51_000L, 1m, commission: 25L);

        // Closing with $1000 profit
        var delta = _settlement.ComputeCashDelta(fill, fillRealizedPnl: 1_000L);

        // 1000 - 25 = 975
        Assert.Equal(975L, delta);
    }

    [Fact]
    public void ComputeCashDelta_CloseWithLoss_DeductsLossAndCommission()
    {
        var fill = TestFills.Sell(TestPerp, 49_000L, 1m, commission: 25L);

        // Closing with $1000 loss
        var delta = _settlement.ComputeCashDelta(fill, fillRealizedPnl: -1_000L);

        // -1000 - 25 = -1025
        Assert.Equal(-1_025L, delta);
    }

    [Fact]
    public void ComputeCashDelta_NoCommission_ReturnsPureRealizedPnl()
    {
        var fill = TestFills.Sell(TestPerp, 51_000L, 1m);

        var delta = _settlement.ComputeCashDelta(fill, fillRealizedPnl: 1_000L);

        Assert.Equal(1_000L, delta);
    }

    #endregion

    #region ComputePositionValue

    [Fact]
    public void ComputePositionValue_LongInProfit_ReturnsUnrealizedPnl()
    {
        var position = new Position(TestPerp, quantity: 1m, averageEntryPrice: 50_000L);

        var value = _settlement.ComputePositionValue(position, currentPrice: 51_000L);

        // (51000 - 50000) * 1 * 1 = 1000
        Assert.Equal(1_000L, value);
    }

    [Fact]
    public void ComputePositionValue_LongInLoss_ReturnsNegativeUnrealizedPnl()
    {
        var position = new Position(TestPerp, quantity: 1m, averageEntryPrice: 50_000L);

        var value = _settlement.ComputePositionValue(position, currentPrice: 49_000L);

        // (49000 - 50000) * 1 * 1 = -1000
        Assert.Equal(-1_000L, value);
    }

    [Fact]
    public void ComputePositionValue_ShortInProfit_ReturnsPositiveUnrealizedPnl()
    {
        var position = new Position(TestPerp, quantity: -1m, averageEntryPrice: 50_000L);

        var value = _settlement.ComputePositionValue(position, currentPrice: 49_000L);

        // (49000 - 50000) * -1 * 1 = 1000
        Assert.Equal(1_000L, value);
    }

    [Fact]
    public void ComputePositionValue_FlatPosition_ReturnsZero()
    {
        var position = new Position(TestPerp);

        var value = _settlement.ComputePositionValue(position, currentPrice: 50_000L);

        Assert.Equal(0L, value);
    }

    #endregion

    #region ValidateSettlement

    [Fact]
    public void ValidateSettlement_Buy_SufficientMargin_ReturnsNull()
    {
        var order = CreateOrder(TestPerp, OrderSide.Buy, 1m);
        var portfolio = CreatePortfolio(10_000L);

        // margin = 50000 * 1 * 1 * 0.10 = 5000 <= 10000
        var result = _settlement.ValidateSettlement(order, 50_000L, portfolio, commission: 0L, NoPrices);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSettlement_Buy_InsufficientMargin_ReturnsError()
    {
        var order = CreateOrder(TestPerp, OrderSide.Buy, 1m);
        var portfolio = CreatePortfolio(3_000L);

        // margin = 50000 * 1 * 1 * 0.10 = 5000 > 3000
        var result = _settlement.ValidateSettlement(order, 50_000L, portfolio, commission: 0L, NoPrices);

        Assert.Equal("Insufficient margin", result);
    }

    [Fact]
    public void ValidateSettlement_Sell_SymmetricWithBuy()
    {
        // Futures: shorts use the same margin as longs (symmetric)
        var order = CreateOrder(TestPerp, OrderSide.Sell, 1m);
        var portfolio = CreatePortfolio(10_000L);

        var result = _settlement.ValidateSettlement(order, 50_000L, portfolio, commission: 0L, NoPrices);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSettlement_CommissionIncludedInMarginCheck()
    {
        var order = CreateOrder(TestPerp, OrderSide.Buy, 1m);
        // margin = 5000, commission = 100, total = 5100 > 5050
        var portfolio = CreatePortfolio(5_050L);

        var result = _settlement.ValidateSettlement(order, 50_000L, portfolio, commission: 100L, NoPrices);

        Assert.Equal("Insufficient margin", result);
    }

    [Fact]
    public void ValidateSettlement_NoMarginRequirement_UsesFullNotional()
    {
        // Asset without MarginRequirement defaults to 1.0 (full notional)
        var asset = new FutureAsset { Name = "NQ", Exchange = "CME", Multiplier = 20m, TickSize = 0.25m };
        var order = CreateOrder(asset, OrderSide.Buy, 1m);
        var portfolio = CreatePortfolio(100_000L);

        // margin = 20000 * 1 * 20 * 1.0 = 400000 > 100000
        var result = _settlement.ValidateSettlement(order, 20_000L, portfolio, commission: 0L, NoPrices);

        Assert.Equal("Insufficient margin", result);
    }

    [Fact]
    public void ValidateSettlement_ExistingLosingPosition_ReducesAvailableMargin()
    {
        // Open a long position at 50000
        var portfolio = CreatePortfolio(10_000L);
        portfolio.Apply(TestFills.Buy(TestPerp, 50_000L, 1m));

        // Now try to open another position. Cash is still 10000 (margin settlement),
        // but price dropped to 40000 → unrealized PnL = -10000
        // Equity = 10000 + (-10000) = 0, UsedMargin = 1 * 50000 * 0.10 = 5000
        // AvailableMargin = 0 - 5000 = -5000 → should reject
        var order = CreateOrder(TestPerp, OrderSide.Buy, 1m);
        var prices = new Dictionary<string, long> { ["BTCUSDT_PERP"] = 40_000L };

        var result = _settlement.ValidateSettlement(order, 40_000L, portfolio, commission: 0L, prices);

        Assert.Equal("Insufficient margin", result);
    }

    #endregion

    #region Portfolio integration — margin settlement round-trip

    [Fact]
    public void Portfolio_MarginRoundTrip_OpenAndCloseWithProfit()
    {
        var portfolio = CreatePortfolio(100_000L);

        // Open long: only commission deducted
        portfolio.Apply(TestFills.Buy(TestPerp, 50_000L, 1m, commission: 25L));
        Assert.Equal(99_975L, portfolio.Cash);

        // Close long: realized PnL + commission
        portfolio.Apply(TestFills.Sell(TestPerp, 51_000L, 1m, commission: 25L));
        // Cash: 99975 + (1000 realized - 25 commission) = 100950
        Assert.Equal(100_950L, portfolio.Cash);
    }

    [Fact]
    public void Portfolio_MarginRoundTrip_OpenAndCloseWithLoss()
    {
        var portfolio = CreatePortfolio(100_000L);

        // Open long
        portfolio.Apply(TestFills.Buy(TestPerp, 50_000L, 1m, commission: 25L));

        // Close at loss
        portfolio.Apply(TestFills.Sell(TestPerp, 49_000L, 1m, commission: 25L));
        // Cash: 99975 + (-1000 realized - 25 commission) = 98950
        Assert.Equal(98_950L, portfolio.Cash);
    }

    [Fact]
    public void Portfolio_MarginEquity_ReflectsUnrealizedPnl()
    {
        var portfolio = CreatePortfolio(100_000L);
        portfolio.Apply(TestFills.Buy(TestPerp, 50_000L, 1m));

        // Equity = Cash + UnrealizedPnl = 100000 + (51000-50000)*1*1 = 101000
        Assert.Equal(101_000L, portfolio.Equity(new Dictionary<string, long> { ["BTCUSDT_PERP"] = 51_000L }));
        // Equity at loss: 100000 + (49000-50000)*1*1 = 99000
        Assert.Equal(99_000L, portfolio.Equity(new Dictionary<string, long> { ["BTCUSDT_PERP"] = 49_000L }));
    }

    [Fact]
    public void Portfolio_MarginShort_SymmetricBehavior()
    {
        var portfolio = CreatePortfolio(100_000L);

        // Open short
        portfolio.Apply(TestFills.Sell(TestPerp, 50_000L, 1m));
        Assert.Equal(100_000L, portfolio.Cash); // Only commission (0) deducted

        // Price drops = profit for short
        // Equity = 100000 + (49000-50000)*(-1)*1 = 100000 + 1000 = 101000
        Assert.Equal(101_000L, portfolio.Equity(new Dictionary<string, long> { ["BTCUSDT_PERP"] = 49_000L }));
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
