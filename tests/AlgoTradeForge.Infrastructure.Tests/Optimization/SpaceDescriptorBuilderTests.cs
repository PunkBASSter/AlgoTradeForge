using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.BuyAndHold;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Infrastructure.Optimization;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Optimization;

public class SpaceDescriptorBuilderTests
{
    [Fact]
    public void Build_DiscoversBuyAndHoldStrategy()
    {
        var builder = new SpaceDescriptorBuilder([typeof(BuyAndHoldStrategy).Assembly]);
        var descriptors = builder.Build();

        Assert.True(descriptors.ContainsKey("BuyAndHold"));
    }

    [Fact]
    public void BuyAndHold_HasOneOptimizableAxis()
    {
        var builder = new SpaceDescriptorBuilder([typeof(BuyAndHoldStrategy).Assembly]);
        var descriptor = builder.GetDescriptor("BuyAndHold");

        Assert.NotNull(descriptor);
        Assert.Equal(typeof(BuyAndHoldStrategy), descriptor!.StrategyType);
        Assert.Equal(typeof(BuyAndHoldParams), descriptor.ParamsType);

        var numericAxes = descriptor.Axes.OfType<NumericRangeAxis>().ToList();
        Assert.Single(numericAxes);

        var qtyAxis = numericAxes.Single(a => a.Name == "Quantity");
        Assert.Equal(0.1m, qtyAxis.Min);
        Assert.Equal(10m, qtyAxis.Max);
        Assert.Equal(0.1m, qtyAxis.Step);
        Assert.Equal(typeof(decimal), qtyAxis.ClrType);
    }

    [Fact]
    public void GetParameterDefaults_ReturnsPropertyInitializerValues()
    {
        var builder = new SpaceDescriptorBuilder([typeof(BuyAndHoldStrategy).Assembly]);
        var descriptor = builder.GetDescriptor("BuyAndHold")!;

        var defaults = builder.GetParameterDefaults(descriptor);

        Assert.Equal(1m, defaults["Quantity"]);
    }

    [Fact]
    public void GetParameterDefaults_ExcludesDataSubscriptions()
    {
        var builder = new SpaceDescriptorBuilder([typeof(BuyAndHoldStrategy).Assembly]);
        var descriptor = builder.GetDescriptor("BuyAndHold")!;

        var defaults = builder.GetParameterDefaults(descriptor);

        Assert.False(defaults.ContainsKey("DataSubscriptions"));
    }

    [Fact]
    public void GetDescriptor_UnknownStrategy_ReturnsNull()
    {
        var builder = new SpaceDescriptorBuilder([typeof(BuyAndHoldStrategy).Assembly]);
        var descriptor = builder.GetDescriptor("NonExistent");

        Assert.Null(descriptor);
    }

    [Fact]
    public void Build_CachesResult()
    {
        var builder = new SpaceDescriptorBuilder([typeof(BuyAndHoldStrategy).Assembly]);

        var first = builder.Build();
        var second = builder.Build();

        Assert.Same(first, second);
    }

    [Fact]
    public void ModuleSlot_DiscoveredCorrectly()
    {
        // Scan only the test assembly (no negative fixtures have [StrategyKey])
        var builder = new SpaceDescriptorBuilder(
            [typeof(StrategyWithModule).Assembly]);
        var descriptor = builder.GetDescriptor("WithModule");

        Assert.NotNull(descriptor);
        var moduleAxes = descriptor!.Axes.OfType<ModuleSlotAxis>().ToList();
        Assert.Single(moduleAxes);

        var slot = moduleAxes[0];
        Assert.Equal("TestModule", slot.Name);
        Assert.Equal(typeof(ITestModule), slot.ModuleInterface);
        Assert.Single(slot.Variants);

        var variant = slot.Variants[0];
        Assert.Equal("TestImpl", variant.TypeKey);
        Assert.Single(variant.Axes);

        var subAxis = variant.Axes[0] as NumericRangeAxis;
        Assert.NotNull(subAxis);
        Assert.Equal("Value", subAxis!.Name);
    }
}

// Negative test fixtures — no [StrategyKey] to avoid polluting assembly scan

public sealed class BadStrategyWithNonNumericOptimizable(BadNonNumericParams p) : StrategyBase<BadNonNumericParams>(p)
{
    public override string Version => "1.0.0";
    public override void OnBarComplete(Domain.History.Int64Bar bar, DataSubscription sub, IOrderContext orders) { }
}

public class BadNonNumericParams : StrategyParamsBase
{
    [Optimizable(Min = 0, Max = 1, Step = 1)]
    public string BadProp { get; init; } = "";
}

// Module slot discovery fixture
public interface ITestModule;

[ModuleKey("TestImpl")]
public class TestModuleImpl : ITestModule
{
    public TestModuleImpl(TestModuleParams _) { }
}

public class TestModuleParams : ModuleParamsBase
{
    [Optimizable(Min = 1, Max = 10, Step = 1)]
    public int Value { get; init; } = 5;
}

[StrategyKey("WithModule")]
public sealed class StrategyWithModule(StrategyWithModuleParams p, IIndicatorFactory? indicators = null) : StrategyBase<StrategyWithModuleParams>(p, indicators)
{
    public override string Version => "1.0.0";
    public override void OnBarComplete(Domain.History.Int64Bar bar, DataSubscription sub, IOrderContext orders) { }
}

public class StrategyWithModuleParams : StrategyParamsBase
{
    [OptimizableModule]
    public ITestModule? TestModule { get; init; }
}
