using AlgoTradeForge.WebApi.Contracts;

namespace AlgoTradeForge.WebApi.Tests.Contracts;

public class FitnessWeightsInputTests
{
    [Fact]
    public void ToFitnessConfig_MapsAllFields()
    {
        var input = new FitnessWeightsInput
        {
            SharpeWeight = 0.4,
            SortinoWeight = 0.3,
            ProfitFactorWeight = 0.2,
            AnnualizedReturnWeight = 0.1,
            MinTrades = 20,
            MaxDrawdownThreshold = 40.0,
        };

        var config = input.ToFitnessConfig();

        Assert.NotNull(config.Weights);
        Assert.Equal(0.4, config.Weights.SharpeWeight);
        Assert.Equal(0.3, config.Weights.SortinoWeight);
        Assert.Equal(0.2, config.Weights.ProfitFactorWeight);
        Assert.Equal(0.1, config.Weights.AnnualizedReturnWeight);
        Assert.Equal(20, config.MinTrades);
        Assert.Equal(40.0, config.MaxDrawdownThreshold);
    }

    [Fact]
    public void ToFitnessConfig_DefaultInput_HasExpectedDefaults()
    {
        var input = new FitnessWeightsInput();

        var config = input.ToFitnessConfig();

        Assert.NotNull(config.Weights);
        Assert.Equal(0.5, config.Weights.SharpeWeight);
        Assert.Equal(0.2, config.Weights.SortinoWeight);
        Assert.Equal(0.15, config.Weights.ProfitFactorWeight);
        Assert.Equal(0.15, config.Weights.AnnualizedReturnWeight);
        Assert.Equal(10, config.MinTrades);
        Assert.Equal(30.0, config.MaxDrawdownThreshold);
    }
}
