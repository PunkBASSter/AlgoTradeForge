using AlgoTradeForge.Application.Optimization;
using AlgoTradeForge.Domain.Optimization.Attributes;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Application.Tests.Optimization;

public class OptimizationAxisResolverTests
{
    private readonly OptimizationAxisResolver _resolver = new();

    private static OptimizationSpaceDescriptor MakeDescriptor(params ParameterAxis[] axes) =>
        new("TestStrategy", typeof(object), typeof(object), axes);

    [Fact]
    public void RangeOverride_WithQuoteAssetUnit_ScalesByTickSize()
    {
        // tickSize=0.01 → scaleFactor=100
        var axis = new NumericRangeAxis("MinThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset);
        var descriptor = MakeDescriptor(axis);
        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["MinThreshold"] = new RangeOverride(50, 150, 50)
        };

        var result = _resolver.Resolve(descriptor, overrides, tickSize: 0.01m);

        var numeric = Assert.IsType<ResolvedNumericAxis>(Assert.Single(result));
        // 50*100=5000, 100*100=10000, 150*100=15000
        Assert.Equal(3, numeric.Values.Count);
        Assert.Equal(5000L, numeric.Values[0]);
        Assert.Equal(10000L, numeric.Values[1]);
        Assert.Equal(15000L, numeric.Values[2]);
    }

    [Fact]
    public void RangeOverride_WithRawUnit_NoScaling()
    {
        var axis = new NumericRangeAxis("Period", 5, 50, 1, typeof(int), ParamUnit.Raw);
        var descriptor = MakeDescriptor(axis);
        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Period"] = new RangeOverride(10, 12, 1)
        };

        var result = _resolver.Resolve(descriptor, overrides, tickSize: 0.01m);

        var numeric = Assert.IsType<ResolvedNumericAxis>(Assert.Single(result));
        Assert.Equal(3, numeric.Values.Count);
        Assert.Equal(10, numeric.Values[0]);
        Assert.Equal(11, numeric.Values[1]);
        Assert.Equal(12, numeric.Values[2]);
    }

    [Fact]
    public void FixedOverride_WithQuoteAssetUnit_ScalesByTickSize()
    {
        var axis = new NumericRangeAxis("AtrMin", 0, 50, 0.5m, typeof(long), ParamUnit.QuoteAsset);
        var descriptor = MakeDescriptor(axis);
        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["AtrMin"] = new FixedOverride(25m)
        };

        var result = _resolver.Resolve(descriptor, overrides, tickSize: 0.01m);

        var numeric = Assert.IsType<ResolvedNumericAxis>(Assert.Single(result));
        Assert.Single(numeric.Values);
        Assert.Equal(2500L, numeric.Values[0]);
    }

    [Fact]
    public void DiscreteSetOverride_WithQuoteAssetUnit_ScalesByTickSize()
    {
        var axis = new NumericRangeAxis("MinThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset);
        var descriptor = MakeDescriptor(axis);
        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["MinThreshold"] = new DiscreteSetOverride([100m, 200m])
        };

        var result = _resolver.Resolve(descriptor, overrides, tickSize: 0.01m);

        var numeric = Assert.IsType<ResolvedNumericAxis>(Assert.Single(result));
        Assert.Equal(2, numeric.Values.Count);
        Assert.Equal(10000L, numeric.Values[0]);
        Assert.Equal(20000L, numeric.Values[1]);
    }

    [Fact]
    public void QuoteAssetUnit_WithoutTickSize_Throws()
    {
        var axis = new NumericRangeAxis("MinThreshold", 50, 500, 50, typeof(long), ParamUnit.QuoteAsset);
        var descriptor = MakeDescriptor(axis);
        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["MinThreshold"] = new RangeOverride(50, 150, 50)
        };

        Assert.Throws<InvalidOperationException>(
            () => _resolver.Resolve(descriptor, overrides, tickSize: null));
    }

    [Fact]
    public void ModuleSubAxes_WithQuoteAssetUnit_ScalesByTickSize()
    {
        var subAxis = new NumericRangeAxis("MinAtr", 0, 50, 0.5m, typeof(long), ParamUnit.QuoteAsset);
        var variant = new ModuleVariantDescriptor("AtrFilter", typeof(object), typeof(object), [subAxis]);
        var moduleAxis = new ModuleSlotAxis("Filter", typeof(object), [variant]);
        var descriptor = MakeDescriptor(moduleAxis);
        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Filter"] = new ModuleChoiceOverride(new Dictionary<string, Dictionary<string, OptimizationAxisOverride>?>
            {
                ["AtrFilter"] = new Dictionary<string, OptimizationAxisOverride>
                {
                    ["MinAtr"] = new FixedOverride(10m)
                }
            })
        };

        var result = _resolver.Resolve(descriptor, overrides, tickSize: 0.01m);

        var moduleSlot = Assert.IsType<ResolvedModuleSlotAxis>(Assert.Single(result));
        Assert.Single(moduleSlot.Variants);
        var variantResult = moduleSlot.Variants[0];
        var numericSubAxis = Assert.IsType<ResolvedNumericAxis>(Assert.Single(variantResult.SubAxes));
        Assert.Single(numericSubAxis.Values);
        Assert.Equal(1000L, numericSubAxis.Values[0]);
    }

    [Fact]
    public void RangeOverride_WithRawUnit_NoTickSize_Succeeds()
    {
        // Raw axes should work fine even without tickSize
        var axis = new NumericRangeAxis("Period", 5, 50, 1, typeof(int));
        var descriptor = MakeDescriptor(axis);
        var overrides = new Dictionary<string, OptimizationAxisOverride>
        {
            ["Period"] = new RangeOverride(10, 12, 1)
        };

        var result = _resolver.Resolve(descriptor, overrides);

        var numeric = Assert.IsType<ResolvedNumericAxis>(Assert.Single(result));
        Assert.Equal(3, numeric.Values.Count);
    }
}
