using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization.Genetic;

public class GeneticConfigResolverTests
{
    [Fact]
    public void Resolve_AllZeros_AutoSizesFromAxes()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("A", [1m, 2m, 3m]),
            new ResolvedNumericAxis("B", [10m, 20m]),
            new ResolvedDiscreteAxis("C", ["x", "y"]),
        };

        var config = GeneticConfigResolver.Resolve(new GeneticConfig(), axes);

        Assert.True(config.PopulationSize >= 50);
        Assert.True(config.PopulationSize <= 500);
        Assert.True(config.MaxEvaluations > 0);
        Assert.True(config.MaxGenerations > 0);
        Assert.True(config.MutationRate > 0);
    }

    [Fact]
    public void Resolve_ExplicitValues_PreservesOverrides()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("A", [1m, 2m, 3m]),
        };

        var config = GeneticConfigResolver.Resolve(new GeneticConfig
        {
            PopulationSize = 100,
            MaxEvaluations = 5000,
            MaxGenerations = 50,
            MutationRate = 0.25,
        }, axes);

        Assert.Equal(100, config.PopulationSize);
        Assert.Equal(5000, config.MaxEvaluations);
        Assert.Equal(50, config.MaxGenerations);
        Assert.Equal(0.25, config.MutationRate);
    }

    [Theory]
    [InlineData(5, 50)]   // small space → floor of 50
    [InlineData(100, 100)] // medium space → 10*sqrt(100) = 100
    [InlineData(2500, 500)] // large space → cap of 500
    public void Resolve_PopulationSize_ScalesWithDimensions(int dims, int expectedPop)
    {
        // Create N numeric axes
        var axes = new List<ResolvedAxis>();
        for (var i = 0; i < dims; i++)
            axes.Add(new ResolvedNumericAxis($"P{i}", [1m, 2m]));

        var config = GeneticConfigResolver.Resolve(new GeneticConfig(), axes);
        Assert.Equal(expectedPop, config.PopulationSize);
    }

    [Fact]
    public void Resolve_MaxGenerations_DerivedFromBudget()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("A", [1m, 2m]),
        };

        var config = GeneticConfigResolver.Resolve(new GeneticConfig
        {
            PopulationSize = 50,
            MaxEvaluations = 10_000,
        }, axes);

        Assert.Equal(200, config.MaxGenerations); // 10000 / 50
    }

    [Fact]
    public void ComputeEffectiveDimensions_NumericAndDiscrete()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("A", [1m, 2m]),
            new ResolvedNumericAxis("B", [10m, 20m]),
            new ResolvedDiscreteAxis("C", ["x", "y"]),
        };

        Assert.Equal(3, GeneticConfigResolver.ComputeEffectiveDimensions(axes));
    }

    [Fact]
    public void ComputeEffectiveDimensions_WithModule()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("A", [1m, 2m]),
            new ResolvedModuleSlotAxis("Mod",
            [
                new ResolvedModuleVariant("V1",
                [
                    new ResolvedNumericAxis("SubA", [1m, 2m]),
                    new ResolvedNumericAxis("SubB", [3m, 4m]),
                ]),
                new ResolvedModuleVariant("V2",
                [
                    new ResolvedNumericAxis("SubC", [5m, 6m]),
                ])
            ])
        };

        // 1 (A) + module(1 variant-selection + max(2, 1) sub) = 4
        Assert.Equal(4, GeneticConfigResolver.ComputeEffectiveDimensions(axes));
    }

    [Fact]
    public void Resolve_MutationRate_AutoFromDimensions()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("A", [1m, 2m]),
            new ResolvedNumericAxis("B", [10m, 20m]),
            new ResolvedNumericAxis("C", [100m, 200m]),
            new ResolvedNumericAxis("D", [1000m, 2000m]),
            new ResolvedDiscreteAxis("E", ["x", "y"]),
        };

        var config = GeneticConfigResolver.Resolve(new GeneticConfig(), axes);
        Assert.Equal(1.0 / 5, config.MutationRate, precision: 10);
    }
}
