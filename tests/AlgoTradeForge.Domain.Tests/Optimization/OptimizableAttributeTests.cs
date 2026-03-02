using AlgoTradeForge.Domain.Optimization.Attributes;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization;

public class OptimizableAttributeTests
{
    [Fact]
    public void OptimizableAttribute_HasCorrectProperties()
    {
        var attr = new OptimizableAttribute { Min = 1.0, Max = 20.0, Step = 0.5 };

        Assert.Equal(1.0, attr.Min);
        Assert.Equal(20.0, attr.Max);
        Assert.Equal(0.5, attr.Step);
    }

    [Fact]
    public void OptimizableAttribute_TargetsProperty()
    {
        var usage = typeof(OptimizableAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        Assert.Equal(AttributeTargets.Property, usage.ValidOn);
    }

    [Fact]
    public void OptimizableModuleAttribute_TargetsProperty()
    {
        var usage = typeof(OptimizableModuleAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        Assert.Equal(AttributeTargets.Property, usage.ValidOn);
    }

    [Fact]
    public void ModuleKeyAttribute_StoresKey()
    {
        var attr = new ModuleKeyAttribute("TestModule");
        Assert.Equal("TestModule", attr.Key);
    }

    [Fact]
    public void ModuleKeyAttribute_TargetsClass()
    {
        var usage = typeof(ModuleKeyAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
    }

    [Fact]
    public void StrategyKeyAttribute_StoresKey()
    {
        var attr = new StrategyKeyAttribute("BuyAndHold");
        Assert.Equal("BuyAndHold", attr.Key);
    }

    [Fact]
    public void StrategyKeyAttribute_TargetsClass()
    {
        var usage = typeof(StrategyKeyAttribute)
            .GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>()
            .Single();

        Assert.Equal(AttributeTargets.Class, usage.ValidOn);
    }
}
