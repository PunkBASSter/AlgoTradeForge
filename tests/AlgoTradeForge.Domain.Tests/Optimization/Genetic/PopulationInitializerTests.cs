using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization.Genetic;

public class PopulationInitializerTests
{
    [Fact]
    public void Create_CorrectSize()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("X", [1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m])
        };

        var population = PopulationInitializer.Create(axes, 30, new Random(42));
        Assert.Equal(30, population.Count);
    }

    [Fact]
    public void Create_StratifiedNumeric_CoversBounds()
    {
        var values = Enumerable.Range(1, 100).Select(i => (object)(decimal)i).ToList();
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("X", values)
        };

        var population = PopulationInitializer.Create(axes, 50, new Random(42));

        // Stratified init should cover the range well
        var geneValues = population
            .Select(c => ((NumericGene)c.Genes["X"]).Value)
            .OrderBy(v => v)
            .ToList();

        // First should be near min, last near max
        Assert.True(geneValues[0] < 10, $"First value {geneValues[0]} should be near min");
        Assert.True(geneValues[^1] > 90, $"Last value {geneValues[^1]} should be near max");
    }

    [Fact]
    public void Create_DiscreteGene_CyclesEvenly()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedDiscreteAxis("Mode", ["A", "B", "C"])
        };

        var population = PopulationInitializer.Create(axes, 9, new Random(42));

        var indexCounts = population
            .Select(c => ((DiscreteGene)c.Genes["Mode"]).Index)
            .GroupBy(i => i)
            .ToDictionary(g => g.Key, g => g.Count());

        // Each choice should appear exactly 3 times
        Assert.Equal(3, indexCounts[0]);
        Assert.Equal(3, indexCounts[1]);
        Assert.Equal(3, indexCounts[2]);
    }

    [Fact]
    public void Create_ModuleAxis_CyclesVariants()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedModuleSlotAxis("Exit",
            [
                new ResolvedModuleVariant("V1", []),
                new ResolvedModuleVariant("V2", [])
            ])
        };

        var population = PopulationInitializer.Create(axes, 10, new Random(42));

        var variantCounts = population
            .Select(c => ((ModuleGene)c.Genes["Exit"]).VariantIndex)
            .GroupBy(i => i)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(5, variantCounts[0]);
        Assert.Equal(5, variantCounts[1]);
    }

    [Fact]
    public void Create_AllGenesInBounds()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("X", [10m, 15m, 20m, 25m, 30m])
        };

        var population = PopulationInitializer.Create(axes, 100, new Random(42));

        foreach (var c in population)
        {
            var gene = (NumericGene)c.Genes["X"];
            Assert.InRange(gene.Value, 10.0, 30.0);
        }
    }
}
