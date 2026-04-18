using System.Reflection;
using System.Text.Json;
using AlgoTradeForge.Domain.Indicators;
using AlgoTradeForge.Domain.Optimization.Space;
using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Strategy.BuyAndHold;
using AlgoTradeForge.Domain.Strategy.DonchianBreakout;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using AlgoTradeForge.Domain.Strategy.Modules.TrailingStop;
using AlgoTradeForge.Infrastructure.Optimization;
using Xunit;

namespace AlgoTradeForge.Infrastructure.Tests.Optimization;

public class OptimizationStrategyFactoryTests
{
    private readonly SpaceDescriptorBuilder _builder;
    private readonly OptimizationStrategyFactory _factory;

    public OptimizationStrategyFactoryTests()
    {
        _builder = new SpaceDescriptorBuilder([typeof(BuyAndHoldStrategy).Assembly]);
        _factory = new OptimizationStrategyFactory(_builder);
    }

    [Fact]
    public void Create_WithDictionary_SetsParameters()
    {
        var strategy = _factory.Create("BuyAndHold", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
        {
            ["Quantity"] = 5m,
        });

        Assert.NotNull(strategy);
        Assert.IsType<BuyAndHoldStrategy>(strategy);
    }

    [Fact]
    public void Create_WithCombination_SetsParameters()
    {
        var combination = new ParameterCombination(new Dictionary<string, object>
        {
            ["Quantity"] = 5m,
        });

        var strategy = _factory.Create("BuyAndHold", combination);

        Assert.NotNull(strategy);
        Assert.IsType<BuyAndHoldStrategy>(strategy);
    }

    [Fact]
    public void Create_WithDefaults_UsesDefaultParamValues()
    {
        var strategy = _factory.Create("BuyAndHold", PassthroughIndicatorFactory.Instance);
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
    public void Create_WithDictionary_UnknownProperty_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _factory.Create("BuyAndHold", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
            {
                ["Qantity"] = 8m // typo
            }));
        Assert.Contains("Qantity", ex.Message);
    }

    [Fact]
    public void Create_WithCombination_UnknownProperty_Throws()
    {
        var combination = new ParameterCombination(new Dictionary<string, object>
        {
            ["Nonexistent"] = 42m
        });

        var ex = Assert.Throws<ArgumentException>(() =>
            _factory.Create("BuyAndHold", combination));
        Assert.Contains("Nonexistent", ex.Message);
    }

    [Fact]
    public void Create_DataSubscriptionsKey_SilentlySkipped()
    {
        var strategy = _factory.Create("BuyAndHold", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
        {
            ["DataSubscriptions"] = new object()
        });

        Assert.NotNull(strategy);
    }

    [Fact]
    public void Create_WithStringEncodedModuleParams_DeserializesCorrectly()
    {
        var strategy = _factory.Create("DonchianBreakout", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
        {
            ["TrailingStopConfig"] = """{"variant":0,"atrMultiplier":3.5,"atrPeriod":20,"donchianPeriod":30}""",
            ["RegimeDetectorConfig"] = """{"adxPeriod":20,"trendThreshold":30}""",
            ["TradeRegistry"] = """{"maxConcurrentGroups":0}""",
        });

        Assert.IsType<DonchianBreakoutStrategy>(strategy);
        var trailingStop = GetParams<DonchianParams>(strategy).TrailingStopConfig;
        Assert.Equal(TrailingStopVariant.Atr, trailingStop.Variant);
        Assert.Equal(3.5, trailingStop.AtrMultiplier);
        Assert.Equal(20, trailingStop.AtrPeriod);
        Assert.Equal(30, trailingStop.DonchianPeriod);
    }

    [Fact]
    public void Create_WithJsonElementStringModuleParams_DeserializesCorrectly()
    {
        // Simulate API path: JsonElement with ValueKind.String containing embedded JSON
        using var doc = JsonDocument.Parse("""
        {
            "TrailingStopConfig": "{\"variant\":0,\"atrMultiplier\":3.5,\"atrPeriod\":20,\"donchianPeriod\":30}",
            "RegimeDetectorConfig": "{\"adxPeriod\":20,\"trendThreshold\":30}"
        }
        """);
        var root = doc.RootElement;

        var strategy = _factory.Create("DonchianBreakout", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
        {
            ["TrailingStopConfig"] = root.GetProperty("TrailingStopConfig"),
            ["RegimeDetectorConfig"] = root.GetProperty("RegimeDetectorConfig"),
        });

        Assert.IsType<DonchianBreakoutStrategy>(strategy);
        var trailingStop = GetParams<DonchianParams>(strategy).TrailingStopConfig;
        Assert.Equal(3.5, trailingStop.AtrMultiplier);
        Assert.Equal(20, trailingStop.AtrPeriod);
    }

    [Fact]
    public void Create_WithJsonElementObjectModuleParams_DeserializesCorrectly()
    {
        // Simulate API path: JsonElement with ValueKind.Object (camelCase keys)
        using var doc = JsonDocument.Parse("""
        {
            "TrailingStopConfig": {"variant":0,"atrMultiplier":3.5,"atrPeriod":20,"donchianPeriod":30}
        }
        """);
        var root = doc.RootElement;

        var strategy = _factory.Create("DonchianBreakout", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
        {
            ["TrailingStopConfig"] = root.GetProperty("TrailingStopConfig"),
        });

        Assert.IsType<DonchianBreakoutStrategy>(strategy);
        var trailingStop = GetParams<DonchianParams>(strategy).TrailingStopConfig;
        Assert.Equal(3.5, trailingStop.AtrMultiplier);
        Assert.Equal(30, trailingStop.DonchianPeriod);
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

    [Fact]
    public void Create_EmptyModuleSlot_KeepsDefault()
    {
        var strategy = _factory.Create("DonchianBreakout", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
        {
            ["MoneyManagement"] = JsonDocument.Parse("{}").RootElement,
        });

        Assert.IsType<DonchianBreakoutStrategy>(strategy);
        var mm = GetParams<DonchianParams>(strategy).MoneyManagement;
        Assert.IsType<FixedNotionalModule>(mm);
    }

    [Fact]
    public void Create_ModuleSlotWithoutTypeKey_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            _factory.Create("DonchianBreakout", PassthroughIndicatorFactory.Instance, new Dictionary<string, object>
            {
                ["MoneyManagement"] = JsonDocument.Parse("""{"riskPercent":2}""").RootElement,
            }));
        Assert.Contains("typeKey", ex.Message);
    }

    private static T GetParams<T>(IInt64BarStrategy strategy)
    {
        var prop = strategy.GetType().GetProperty("Params", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Params property not found.");
        return (T)prop.GetValue(strategy)!;
    }
}
