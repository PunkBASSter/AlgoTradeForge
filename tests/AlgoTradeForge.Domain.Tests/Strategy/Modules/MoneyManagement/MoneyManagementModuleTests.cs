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

    private static StrategyContext CreateContext(long cash, long usedMargin = 0L)
    {
        var context = new StrategyContext();
        var bar = new Int64Bar(0, 50000, 51000, 49000, 50000, 1000);
        var orders = Substitute.For<IOrderContext>();
        orders.Cash.Returns(cash);
        orders.UsedMargin.Returns(usedMargin);
        context.Update(bar, DefaultSubscription, orders);
        return context;
    }

    private static MoneyManagementModule CreateModule(double riskPercent = 1.0) =>
        new(new MoneyManagementParams
        {
            Method = SizingMethod.FixedFractional,
            RiskPercent = riskPercent,
        });

    [Fact]
    public void FixedFractional_KnownInputs_ReturnsExpectedQuantity()
    {
        // equity=100000, riskPercent=1%, entry=50000, SL=48000
        // riskDistance = 2000, riskAmount = 100000 * 1.0 / 100 = 1000
        // rawQty = 1000 / 2000 = 0.5
        // RoundQuantityDown(0.5, step=0.001) = 0.5 (exact)
        var module = CreateModule(riskPercent: 1.0);
        var context = CreateContext(cash: 100_000L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 48_000, context, asset);

        Assert.Equal(0.5m, qty);
    }

    [Fact]
    public void FixedFractional_QuantityClampedToMaxOrderQuantity()
    {
        // equity=10_000_000, riskPercent=5%, entry=50000, SL=49999
        // riskDistance=1, riskAmount=10_000_000*5/100=500_000
        // rawQty=500_000 — far exceeds maxOrderQuantity=100
        var module = CreateModule(riskPercent: 5.0);
        var context = CreateContext(cash: 10_000_000L);
        var asset = CreateAsset(maxOrderQuantity: 100m);

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 49_999, context, asset);

        Assert.Equal(100m, qty);
    }

    [Fact]
    public void FixedFractional_QuantityBelowMinOrderQuantity_ReturnsZero()
    {
        // equity=100, riskPercent=1%, entry=50000, SL=48000
        // riskDistance=2000, riskAmount=100*1/100=1
        // rawQty=1/2000=0.0005 → RoundQuantityDown(0.0005, step=0.001) = 0.000
        // 0.000 < minOrderQuantity=0.001 → 0
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
        // entry == stopLoss → riskDistance = 0
        var module = CreateModule(riskPercent: 1.0);
        var context = CreateContext(cash: 100_000L);
        var asset = CreateAsset();

        var qty = module.CalculateSize(entryPrice: 50_000, stopLoss: 50_000, context, asset);

        Assert.Equal(0m, qty);
    }
}
