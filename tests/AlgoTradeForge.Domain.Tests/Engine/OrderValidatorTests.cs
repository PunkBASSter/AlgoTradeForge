using AlgoTradeForge.Domain.Engine;
using AlgoTradeForge.Domain.Trading;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Engine;

public class OrderValidatorTests
{
    private readonly OrderValidator _validator = new();

    private static Asset TestEquity(decimal shortMarginRate = 1.0m) =>
        Asset.Equity("TEST", "NYSE", minOrderQuantity: 1m, maxOrderQuantity: 1000m, quantityStepSize: 1m,
            shortMarginRate: shortMarginRate);

    private static Order CreateOrder(Asset asset, OrderSide side, decimal quantity) =>
        new()
        {
            Id = 1,
            Asset = asset,
            Side = side,
            Type = OrderType.Market,
            Quantity = quantity
        };

    private static BacktestOptions CreateOptions(long initialCash = 100_000L, long commission = 0L) =>
        new()
        {
            InitialCash = initialCash,
            Asset = TestEquity(),
            StartTime = DateTimeOffset.UtcNow,
            EndTime = DateTimeOffset.UtcNow.AddDays(1),
            CommissionPerTrade = commission
        };

    #region ValidateSubmission

    [Fact]
    public void ValidateSubmission_QuantityBelowMinimum_ReturnsRejection()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Buy, 0.5m);

        var result = _validator.ValidateSubmission(order);

        Assert.NotNull(result);
        Assert.Contains("below minimum", result);
    }

    [Fact]
    public void ValidateSubmission_QuantityAboveMaximum_ReturnsRejection()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Buy, 1001m);

        var result = _validator.ValidateSubmission(order);

        Assert.NotNull(result);
        Assert.Contains("above maximum", result);
    }

    [Fact]
    public void ValidateSubmission_QuantityNotAlignedToStep_ReturnsRejection()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Buy, 1.5m);

        var result = _validator.ValidateSubmission(order);

        Assert.NotNull(result);
        Assert.Contains("not aligned to step size", result);
    }

    [Fact]
    public void ValidateSubmission_ValidQuantity_ReturnsNull()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Buy, 10m);

        var result = _validator.ValidateSubmission(order);

        Assert.Null(result);
    }

    #endregion

    #region ValidateSettlement — Buy

    [Fact]
    public void ValidateSettlement_Buy_SufficientCash_ReturnsNull()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Buy, 5m);
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        var result = _validator.ValidateSettlement(order, 10_000L, portfolio, CreateOptions());

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSettlement_Buy_InsufficientCash_ReturnsRejection()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Buy, 5m);
        var portfolio = new Portfolio { InitialCash = 10_000L };
        portfolio.Initialize();

        var result = _validator.ValidateSettlement(order, 10_000L, portfolio, CreateOptions());

        Assert.Equal("Insufficient cash", result);
    }

    [Fact]
    public void ValidateSettlement_Buy_CommissionIncludedInCostCheck()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Buy, 10m);
        // 10 shares * 10_000 = 100_000 exactly equals cash, but commission pushes it over
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        var options = CreateOptions(commission: 1L);

        var result = _validator.ValidateSettlement(order, 10_000L, portfolio, options);

        Assert.Equal("Insufficient cash", result);
    }

    #endregion

    #region ValidateSettlement — Sell (closing long)

    [Fact]
    public void ValidateSettlement_SellClosingLong_NoMarginNeeded_ReturnsNull()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Sell, 5m);
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        // Simulate a long position of 10 shares
        portfolio.Apply(new Fill(1, asset, DateTimeOffset.UtcNow, 10_000L, 10m, OrderSide.Buy, 0L));

        var result = _validator.ValidateSettlement(order, 10_000L, portfolio, CreateOptions());

        Assert.Null(result);
    }

    #endregion

    #region ValidateSettlement — Sell (opening short)

    [Fact]
    public void ValidateSettlement_SellOpeningShort_SufficientMargin_ReturnsNull()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Sell, 5m);
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        // No existing position — full 5 shares are short, margin = 5 * 10_000 * 1 * 1.0 = 50_000 <= 100_000
        var result = _validator.ValidateSettlement(order, 10_000L, portfolio, CreateOptions());

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSettlement_SellOpeningShort_InsufficientMargin_ReturnsRejection()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Sell, 15m);
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();

        // No existing position — full 15 shares short, margin = 15 * 10_000 * 1 * 1.0 = 150_000 > 100_000
        var result = _validator.ValidateSettlement(order, 10_000L, portfolio, CreateOptions());

        Assert.Equal("Insufficient margin for short", result);
    }

    #endregion

    #region ValidateSettlement — Sell (partial close + partial short)

    [Fact]
    public void ValidateSettlement_SellPartialCloseAndPartialShort_OnlyShortPortionRequiresMargin()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Sell, 8m);
        var portfolio = new Portfolio { InitialCash = 100_000L };
        portfolio.Initialize();
        // Long 5 shares — selling 8 means closing 5 long + opening 3 short
        portfolio.Apply(new Fill(1, asset, DateTimeOffset.UtcNow, 10_000L, 5m, OrderSide.Buy, 0L));

        // margin = 3 * 10_000 * 1 * 1.0 = 30_000; cash after buy = 100_000 - 50_000 = 50_000
        var result = _validator.ValidateSettlement(order, 10_000L, portfolio, CreateOptions());

        Assert.Null(result);
    }

    [Fact]
    public void ValidateSettlement_SellPartialCloseAndPartialShort_InsufficientMarginForShortPortion()
    {
        var asset = TestEquity();
        var order = CreateOrder(asset, OrderSide.Sell, 8m);
        var portfolio = new Portfolio { InitialCash = 15_000L };
        portfolio.Initialize();
        // Long 5 shares — selling 8 means closing 5 long + opening 3 short
        portfolio.Apply(new Fill(1, asset, DateTimeOffset.UtcNow, 10_000L, 5m, OrderSide.Buy, 0L));

        // cash after buy = 15_000 - 50_000 = -35_000; margin = 3 * 10_000 = 30_000 > -35_000? No, margin > cash
        var result = _validator.ValidateSettlement(order, 10_000L, portfolio, CreateOptions());

        Assert.Equal("Insufficient margin for short", result);
    }

    #endregion

    #region ShortMarginRate defaults

    [Fact]
    public void DefaultShortMarginRate_Equity_IsOne()
    {
        var asset = Asset.Equity("X", "X");
        Assert.Equal(1.0m, asset.ShortMarginRate);
    }

    [Fact]
    public void DefaultShortMarginRate_Future_IsOne()
    {
        var asset = Asset.Future("X", "X", multiplier: 1m, tickSize: 0.01m);
        Assert.Equal(1.0m, asset.ShortMarginRate);
    }

    [Fact]
    public void DefaultShortMarginRate_Crypto_IsOne()
    {
        var asset = Asset.Crypto("X", "X", decimalDigits: 2);
        Assert.Equal(1.0m, asset.ShortMarginRate);
    }

    #endregion
}
