using AlgoTradeForge.Domain.History;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Tests.TestUtilities;
using NSubstitute;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules.MoneyManagement;

public sealed class MoneyManagementModuleTests
{
    private static readonly DataSubscription DefaultSubscription =
        new(TestAssets.BtcUsdt, TimeSpan.FromHours(1));

    private static CryptoAsset CreateAsset(
        decimal minOrderQuantity = 0.001m,
        decimal maxOrderQuantity = 100m,
        decimal quantityStepSize = 0.001m) =>
        CryptoAsset.Create("BTCUSDT", "binance", decimalDigits: 2,
            minOrderQuantity: minOrderQuantity,
            maxOrderQuantity: maxOrderQuantity,
            quantityStepSize: quantityStepSize);

    private static StrategyContextBase CreateContext(long cash, long usedMargin = 0L)
    {
        var context = new StrategyContextBase();
        var bar = new Int64Bar(0, 50000, 51000, 49000, 50000, 1000);
        var orders = Substitute.For<IOrderContext>();
        orders.Cash.Returns(cash);
        orders.UsedMargin.Returns(usedMargin);
        context.Update(bar, DefaultSubscription, orders);
        return context;
    }

    private static FixedFractionalModule CreateModule(double riskPercent = 1.0) =>
        new(new FixedFractionalParams { RiskPercent = riskPercent });

    [Fact]
    public void FixedFractional_KnownInputs_ReturnsExpectedQuantity()
    {
        var module = CreateModule(riskPercent: 1.0);
        var context = CreateContext(cash: 100_000L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 48_000, context, asset);

        Assert.Equal(0.5m, qty);
    }

    [Fact]
    public void FixedFractional_QuantityClampedToMaxOrderQuantity()
    {
        var module = CreateModule(riskPercent: 5.0);
        var context = CreateContext(cash: 10_000_000L);
        var asset = CreateAsset(maxOrderQuantity: 100m);

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 49_999, context, asset);

        Assert.Equal(100m, qty);
    }

    [Fact]
    public void FixedFractional_QuantityBelowMinOrderQuantity_ReturnsZero()
    {
        var module = CreateModule(riskPercent: 1.0);
        var context = CreateContext(cash: 100L);
        var asset = CreateAsset(minOrderQuantity: 0.001m);

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 48_000, context, asset);

        Assert.Equal(0m, qty);
    }

    [Fact]
    public void FixedFractional_ZeroEquity_ReturnsZero()
    {
        var module = CreateModule(riskPercent: 1.0);
        var context = CreateContext(cash: 0L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 48_000, context, asset);

        Assert.Equal(0m, qty);
    }

    [Fact]
    public void FixedFractional_ZeroRiskDistance_ReturnsZero()
    {
        var module = CreateModule(riskPercent: 1.0);
        var context = CreateContext(cash: 100_000L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 50_000, context, asset);

        Assert.Equal(0m, qty);
    }

    // --- AtrVolTarget Tests ---

    private static AtrVolTargetModule CreateAtrVolTargetModule(double volTarget = 0.15) =>
        new(new AtrVolTargetParams { VolTarget = volTarget });

    private static TestStrategyContext CreateContextWithAtr(long cash, long currentAtr, long usedMargin = 0L)
    {
        var context = new TestStrategyContext { CurrentVolatility = currentAtr };
        var bar = new Int64Bar(0, 50000, 51000, 49000, 50000, 1000);
        var orders = Substitute.For<IOrderContext>();
        orders.Cash.Returns(cash);
        orders.UsedMargin.Returns(usedMargin);
        context.Update(bar, DefaultSubscription, orders);
        return context;
    }

    [Fact]
    public void AtrVolTarget_KnownInputs_ReturnsExpectedQuantity()
    {
        var module = CreateAtrVolTargetModule(volTarget: 0.15);
        var context = CreateContextWithAtr(cash: 100_000L, currentAtr: 500L);
        var asset = CreateAsset(maxOrderQuantity: 1000m);

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 48_000, context, asset);

        Assert.Equal(30m, qty);
    }

    [Fact]
    public void AtrVolTarget_InverselyProportionalToAtr()
    {
        var module = CreateAtrVolTargetModule(volTarget: 0.10);
        var asset = CreateAsset(maxOrderQuantity: 10000m);

        var contextLowAtr = CreateContextWithAtr(cash: 100_000L, currentAtr: 200L);
        var contextHighAtr = CreateContextWithAtr(cash: 100_000L, currentAtr: 1000L);

        var qtyLowAtr = module.CalculateSize(50_000, 48_000, contextLowAtr, asset);
        var qtyHighAtr = module.CalculateSize(50_000, 48_000, contextHighAtr, asset);

        Assert.True(qtyLowAtr > qtyHighAtr, "Lower ATR should produce larger position");
    }

    [Fact]
    public void AtrVolTarget_ZeroAtr_ReturnsZero()
    {
        var module = CreateAtrVolTargetModule(volTarget: 0.15);
        var context = CreateContextWithAtr(cash: 100_000L, currentAtr: 0L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(50_000, 48_000, context, asset);

        Assert.Equal(0m, qty);
    }

    [Fact]
    public void AtrVolTarget_ResultClampedToMaxOrderQuantity()
    {
        var module = CreateAtrVolTargetModule(volTarget: 0.15);
        var context = CreateContextWithAtr(cash: 1_000_000L, currentAtr: 1L);
        var asset = CreateAsset(maxOrderQuantity: 100m);

        var qty = module.CalculateSize(50_000, 48_000, context, asset);

        Assert.Equal(100m, qty);
    }

    // --- HalfKelly Tests ---

    private static HalfKellyModule CreateHalfKellyModule(
        double winRate = 0.5, double payoffRatio = 2.0) =>
        new(new HalfKellyParams { WinRate = winRate, PayoffRatio = payoffRatio });

    [Fact]
    public void HalfKelly_KnownInputs_ReturnsExpectedQuantity()
    {
        var module = CreateHalfKellyModule(winRate: 0.5, payoffRatio: 2.0);
        var context = CreateContext(cash: 100_000L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 48_000, context, asset);

        Assert.Equal(0.25m, qty);
    }

    [Fact]
    public void HalfKelly_HighWinRateHighPayoff_LargerPosition()
    {
        var module = CreateHalfKellyModule(winRate: 0.6, payoffRatio: 3.0);
        var context = CreateContext(cash: 100_000L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 48_000, context, asset);

        Assert.True(qty > 0.25m, "Higher win rate and payoff should produce larger position than base case");
        Assert.True(qty <= 0.467m, $"qty={qty} exceeds expected upper bound");
    }

    [Fact]
    public void HalfKelly_NegativeKellyFraction_ReturnsZero()
    {
        var module = CreateHalfKellyModule(winRate: 0.3, payoffRatio: 1.0);
        var context = CreateContext(cash: 100_000L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 48_000, context, asset);

        Assert.Equal(0m, qty);
    }

    [Fact]
    public void HalfKelly_ZeroEntryPrice_ReturnsZero()
    {
        var module = CreateHalfKellyModule(winRate: 0.5, payoffRatio: 2.0);
        var context = CreateContext(cash: 100_000L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(entryPrice: 0, stopLoss: 48_000, context, asset);

        Assert.Equal(0m, qty);
    }

    [Fact]
    public void HalfKelly_ResultRoundedAndClamped()
    {
        var module = CreateHalfKellyModule(winRate: 0.6, payoffRatio: 3.0);
        var context = CreateContext(cash: 100_000_000L);
        var asset = CreateAsset(maxOrderQuantity: 100m);

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 48_000, context, asset);

        Assert.Equal(100m, qty);
    }
}
