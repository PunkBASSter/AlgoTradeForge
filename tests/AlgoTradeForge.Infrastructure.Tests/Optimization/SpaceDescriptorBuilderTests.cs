using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.ZigZagBreakout;
using AlgoTradeForge.Infrastructure.Optimization;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Optimization;

public class SpaceDescriptorBuilderTests
{
    [Fact]
    public void Build_DiscoversZigZagBreakoutStrategy()
    {
        var builder = new SpaceDescriptorBuilder([typeof(ZigZagBreakoutStrategy).Assembly]);
        var descriptors = builder.Build();

        Assert.True(descriptors.ContainsKey("ZigZagBreakout"));
    }

    [Fact]
    public void ZigZagBreakout_HasThreeOptimizableAxes()
    {
        var builder = new SpaceDescriptorBuilder([typeof(ZigZagBreakoutStrategy).Assembly]);
        var descriptor = builder.GetDescriptor("ZigZagBreakout");

        Assert.NotNull(descriptor);
        Assert.Equal(typeof(ZigZagBreakoutStrategy), descriptor!.StrategyType);
        Assert.Equal(typeof(ZigZagBreakoutParams), descriptor.ParamsType);

        var numericAxes = descriptor.Axes.OfType<NumericRangeAxis>().ToList();
        Assert.Equal(3, numericAxes.Count);

        var dzzAxis = numericAxes.Single(a => a.Name == "DzzDepth");
        Assert.Equal(1m, dzzAxis.Min);
        Assert.Equal(20m, dzzAxis.Max);
        Assert.Equal(0.5m, dzzAxis.Step);
        Assert.Equal(typeof(decimal), dzzAxis.ClrType);

        var thresholdAxis = numericAxes.Single(a => a.Name == "MinimumThreshold");
        Assert.Equal(5_000m, thresholdAxis.Min);
        Assert.Equal(50_000m, thresholdAxis.Max);
        Assert.Equal(typeof(long), thresholdAxis.ClrType);

        var riskAxis = numericAxes.Single(a => a.Name == "RiskPercentPerTrade");
        Assert.Equal(0.5m, riskAxis.Min);
        Assert.Equal(3m, riskAxis.Max);
    }

    [Fact]
    public void GetDescriptor_UnknownStrategy_ReturnsNull()
    {
        var builder = new SpaceDescriptorBuilder([typeof(ZigZagBreakoutStrategy).Assembly]);
        var descriptor = builder.GetDescriptor("NonExistent");

        Assert.Null(descriptor);
    }

    [Fact]
    public void Build_CachesResult()
    {
        var builder = new SpaceDescriptorBuilder([typeof(ZigZagBreakoutStrategy).Assembly]);

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
    public override void OnBarComplete(Domain.History.Int64Bar bar, DataSubscription sub, IOrderContext orders) { }
}

public class StrategyWithModuleParams : StrategyParamsBase
{
    [OptimizableModule]
    public ITestModule? TestModule { get; init; }
}
