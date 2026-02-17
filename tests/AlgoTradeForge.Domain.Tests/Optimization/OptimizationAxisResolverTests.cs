using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization;

public class OptimizationAxisResolverTests
{
    private readonly OptimizationAxisResolver _resolver = new();

    private static IOptimizationSpaceDescriptor CreateDescriptor(params ParameterAxis[] axes)
    {
        return new OptimizationSpaceDescriptor("Test", typeof(object), typeof(object), axes);
    }

    [Fact]
    public void RangeOverride_NarrowsRange()
    {
        var descriptor = CreateDescriptor(
            new NumericRangeAxis("Depth", 1m, 20m, 0.5m, typeof(decimal)));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Depth"] = new RangeOverride(3m, 6m, 1m)
        };

        var resolved = _resolver.Resolve(descriptor, overrides);

        Assert.Single(resolved);
        var numeric = Assert.IsType<ResolvedNumericAxis>(resolved[0]);
        Assert.Equal(4, numeric.Values.Count); // 3, 4, 5, 6
        Assert.Equal(3m, numeric.Values[0]);
        Assert.Equal(6m, numeric.Values[3]);
    }

    [Fact]
    public void FixedOverride_ReturnsSingleValue()
    {
        var descriptor = CreateDescriptor(
            new NumericRangeAxis("Risk", 0.5m, 3m, 0.5m, typeof(decimal)));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Risk"] = new FixedOverride(1.5m)
        };

        var resolved = _resolver.Resolve(descriptor, overrides);
        var numeric = Assert.IsType<ResolvedNumericAxis>(resolved[0]);
        Assert.Single(numeric.Values);
        Assert.Equal(1.5m, numeric.Values[0]);
    }

    [Fact]
    public void OmittedParam_ReturnsEmptyAxis()
    {
        var descriptor = CreateDescriptor(
            new NumericRangeAxis("Depth", 1m, 20m, 0.5m, typeof(decimal)));

        var resolved = _resolver.Resolve(descriptor, null);

        var numeric = Assert.IsType<ResolvedNumericAxis>(resolved[0]);
        Assert.Empty(numeric.Values);
    }

    [Fact]
    public void OverrideExceedsBounds_Throws()
    {
        var descriptor = CreateDescriptor(
            new NumericRangeAxis("Depth", 1m, 20m, 0.5m, typeof(decimal)));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Depth"] = new RangeOverride(0m, 25m, 1m)
        };

        Assert.Throws<ArgumentException>(() => _resolver.Resolve(descriptor, overrides));
    }

    [Fact]
    public void UnknownParamName_Throws()
    {
        var descriptor = CreateDescriptor(
            new NumericRangeAxis("Depth", 1m, 20m, 0.5m, typeof(decimal)));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Unknown"] = new RangeOverride(1m, 5m, 1m)
        };

        Assert.Throws<ArgumentException>(() => _resolver.Resolve(descriptor, overrides));
    }

    [Fact]
    public void ModuleSlot_ValidVariantKey()
    {
        var descriptor = CreateDescriptor(
            new ModuleSlotAxis("Exit", typeof(object),
            [
                new ModuleVariantDescriptor("AtrExit", typeof(object), typeof(object),
                [
                    new NumericRangeAxis("Mult", 1m, 5m, 0.5m, typeof(decimal))
                ])
            ]));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Exit"] = new ModuleChoiceOverride(new Dictionary<string, Dictionary<string, OptimizationAxisOverride>?>
            {
                ["AtrExit"] = new Dictionary<string, OptimizationAxisOverride>
                {
                    ["Mult"] = new RangeOverride(2m, 3m, 0.5m)
                }
            })
        };

        var resolved = _resolver.Resolve(descriptor, overrides);
        var moduleAxis = Assert.IsType<ResolvedModuleSlotAxis>(resolved[0]);
        Assert.Single(moduleAxis.Variants);
        Assert.Equal("AtrExit", moduleAxis.Variants[0].TypeKey);
    }

    [Fact]
    public void ModuleSlot_UnknownVariantKey_Throws()
    {
        var descriptor = CreateDescriptor(
            new ModuleSlotAxis("Exit", typeof(object),
            [
                new ModuleVariantDescriptor("AtrExit", typeof(object), typeof(object), [])
            ]));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Exit"] = new ModuleChoiceOverride(new Dictionary<string, Dictionary<string, OptimizationAxisOverride>?>
            {
                ["BadKey"] = null
            })
        };

        Assert.Throws<ArgumentException>(() => _resolver.Resolve(descriptor, overrides));
    }

    [Fact]
    public void MinGreaterThanMax_Throws()
    {
        var descriptor = CreateDescriptor(
            new NumericRangeAxis("Depth", 1m, 20m, 0.5m, typeof(decimal)));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Depth"] = new RangeOverride(10m, 5m, 1m)
        };

        Assert.Throws<ArgumentException>(() => _resolver.Resolve(descriptor, overrides));
    }

    [Fact]
    public void DiscreteSetOverride_OnNumericAxis_ReturnsSpecificValues()
    {
        var descriptor = CreateDescriptor(
            new NumericRangeAxis("Depth", 1m, 20m, 0.5m, typeof(decimal)));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Depth"] = new DiscreteSetOverride([1m, 5m, 10m, 15m])
        };

        var resolved = _resolver.Resolve(descriptor, overrides);
        var numeric = Assert.IsType<ResolvedNumericAxis>(resolved[0]);
        Assert.Equal(4, numeric.Values.Count);
        Assert.Equal(1m, numeric.Values[0]);
        Assert.Equal(5m, numeric.Values[1]);
        Assert.Equal(10m, numeric.Values[2]);
        Assert.Equal(15m, numeric.Values[3]);
    }

    [Fact]
    public void DiscreteSetOverride_OnNumericAxis_OutOfBounds_Throws()
    {
        var descriptor = CreateDescriptor(
            new NumericRangeAxis("Depth", 1m, 20m, 0.5m, typeof(decimal)));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Depth"] = new DiscreteSetOverride([5m, 25m])
        };

        Assert.Throws<ArgumentException>(() => _resolver.Resolve(descriptor, overrides));
    }

    [Fact]
    public void DiscreteSetOverride_OnDiscreteAxis_ReturnsOverriddenValues()
    {
        var descriptor = CreateDescriptor(
            new DiscreteSetAxis("Mode", ["Fast", "Normal", "Slow"], typeof(string)));

        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Mode"] = new DiscreteSetOverride(["Fast", "Slow"])
        };

        var resolved = _resolver.Resolve(descriptor, overrides);
        var discrete = Assert.IsType<ResolvedDiscreteAxis>(resolved[0]);
        Assert.Equal(2, discrete.Values.Count);
    }
}
