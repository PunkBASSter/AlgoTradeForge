using AlgoTradeForge.Domain.Strategy.Modules;
using AlgoTradeForge.Domain.Strategy.Modules.MoneyManagement;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Strategy.Modules;

public class ModuleDescriptorTests
{
    [Fact]
    public void Describe_DefaultFixedNotional_ShowsTypeKeyAndEffectiveParams()
    {
        var module = new FixedNotionalModule(new FixedNotionalParams());

        var description = ModuleDescriptor.Describe(module);

        Assert.Equal("mm.fixed-notional", description["typeKey"]);
        var moduleParams = Assert.IsType<FixedNotionalParams>(description["params"]);
        Assert.Equal(100_000L, moduleParams.Notional);
    }

    [Fact]
    public void Describe_ConfiguredModule_EchoesConfiguredValues()
    {
        var module = new FixedFractionalModule(new FixedFractionalParams { RiskPercent = 2.5 });

        var description = ModuleDescriptor.Describe(module);

        Assert.Equal("mm.fixed-fractional", description["typeKey"]);
        var moduleParams = Assert.IsType<FixedFractionalParams>(description["params"]);
        Assert.Equal(2.5, moduleParams.RiskPercent);
    }
}
