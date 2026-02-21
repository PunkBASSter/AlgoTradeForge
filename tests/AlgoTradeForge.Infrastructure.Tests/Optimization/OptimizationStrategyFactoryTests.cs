using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy.ZigZagBreakout;
using AlgoTradeForge.Infrastructure.Optimization;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Optimization;

public class OptimizationStrategyFactoryTests
{
    private readonly SpaceDescriptorBuilder _builder;
    private readonly OptimizationStrategyFactory _factory;

    public OptimizationStrategyFactoryTests()
    {
        _builder = new SpaceDescriptorBuilder([typeof(ZigZagBreakoutStrategy).Assembly]);
        _factory = new OptimizationStrategyFactory(_builder);
    }

    [Fact]
    public void Create_WithDictionary_SetsParameters()
    {
        var strategy = _factory.Create("ZigZagBreakout", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
        {
            ["DzzDepth"] = 8m,
            ["MinimumThreshold"] = 50L,
            ["RiskPercentPerTrade"] = 2m
        });

        Assert.NotNull(strategy);
        Assert.IsType<ZigZagBreakoutStrategy>(strategy);
    }

    [Fact]
    public void Create_WithCombination_SetsParameters()
    {
        var combination = new ParameterCombination(new Dictionary<string, object>
        {
            ["DzzDepth"] = 8m,
            ["MinimumThreshold"] = 50L,
            ["RiskPercentPerTrade"] = 2m
        });

        var strategy = _factory.Create("ZigZagBreakout", combination);

        Assert.NotNull(strategy);
        Assert.IsType<ZigZagBreakoutStrategy>(strategy);
    }

    [Fact]
    public void Create_WithDefaults_UsesDefaultParamValues()
    {
        var strategy = _factory.Create("ZigZagBreakout", PassthroughIndicatorFactory.Instance);
        Assert.NotNull(strategy);
    }

    [Fact]
    public void Create_UnknownStrategy_Throws()
    {
        Assert.Throws<ArgumentException>(() => _factory.Create("NonExistent", PassthroughIndicatorFactory.Instance));
    }

    [Fact]
    public void Create_WithCombination_UnknownStrategy_Throws()
    {
        var combination = new ParameterCombination(new Dictionary<string, object>());
        Assert.Throws<ArgumentException>(() => _factory.Create("NonExistent", combination));
    }

    [Fact]
    public void Create_TypeConversion_DecimalToLong()
    {
        var combination = new ParameterCombination(new Dictionary<string, object>
        {
            ["MinimumThreshold"] = 50m
        });

        var strategy = _factory.Create("ZigZagBreakout", combination);
        Assert.NotNull(strategy);
    }

    [Fact]
    public void Create_WithDictionary_UnknownProperty_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _factory.Create("ZigZagBreakout", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
            {
                ["DzzDpeth"] = 8m // typo
            }));
        Assert.Contains("DzzDpeth", ex.Message);
    }

    [Fact]
    public void Create_WithCombination_UnknownProperty_Throws()
    {
        var combination = new ParameterCombination(new Dictionary<string, object>
        {
            ["Nonexistent"] = 42m
        });

        var ex = Assert.Throws<ArgumentException>(() =>
            _factory.Create("ZigZagBreakout", combination));
        Assert.Contains("Nonexistent", ex.Message);
    }

    [Fact]
    public void Create_DataSubscriptionsKey_SilentlySkipped()
    {
        var strategy = _factory.Create("ZigZagBreakout", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
        {
            ["DataSubscriptions"] = new object()
        });

        Assert.NotNull(strategy);
    }

    [Fact]
    public void Create_WithModuleSlot_CreatesModuleInstance()
    {
        var builder = new SpaceDescriptorBuilder(
            [typeof(StrategyWithModule).Assembly]);
        var factory = new OptimizationStrategyFactory(builder);

        var combination = new ParameterCombination(new Dictionary<string, object>
        {
            ["TestModule"] = new ModuleSelection("TestImpl", new Dictionary<string, object>
            {
                ["Value"] = 7
            })
        });

        var strategy = factory.Create("WithModule", combination);
        Assert.NotNull(strategy);
        Assert.IsType<StrategyWithModule>(strategy);
    }
}
