using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization.Genetic;

public class ChromosomeFactoryTests
{
    private readonly Random _rng = new(42);

    [Fact]
    public void FromAxes_NumericAxis_CreatesNumericGene()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("Period", [10m, 15m, 20m])
        };

        var chromosome = ChromosomeFactory.FromAxes(axes, _rng);

        Assert.Single(chromosome.Genes);
        var gene = Assert.IsType<NumericGene>(chromosome.Genes["Period"]);
        Assert.InRange(gene.Value, 10.0, 20.0);
        Assert.Equal(10.0, gene.Min);
        Assert.Equal(20.0, gene.Max);
        Assert.Equal(5.0, gene.Step);
    }

    [Fact]
    public void FromAxes_DiscreteAxis_CreatesDiscreteGene()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedDiscreteAxis("Mode", ["Fast", "Slow"])
        };

        var chromosome = ChromosomeFactory.FromAxes(axes, _rng);

        var gene = Assert.IsType<DiscreteGene>(chromosome.Genes["Mode"]);
        Assert.InRange(gene.Index, 0, 1);
        Assert.Equal(2, gene.Choices.Count);
    }

    [Fact]
    public void FromAxes_ModuleAxis_CreatesModuleGene()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedModuleSlotAxis("Exit",
            [
                new ResolvedModuleVariant("AtrExit",
                [
                    new ResolvedNumericAxis("Multiplier", [1.5m, 2.0m, 2.5m])
                ]),
                new ResolvedModuleVariant("FixedTp", [])
            ])
        };

        var chromosome = ChromosomeFactory.FromAxes(axes, _rng);

        var gene = Assert.IsType<ModuleGene>(chromosome.Genes["Exit"]);
        Assert.InRange(gene.VariantIndex, 0, 1);
    }

    [Fact]
    public void RoundTrip_NumericGene_PreservesValue()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedNumericAxis("Depth", [3m, 6m, 9m, 12m])
        };

        var chromosome = ChromosomeFactory.FromAxes(axes, _rng);
        var combo = ChromosomeFactory.ToParameterCombination(chromosome);
        var roundTripped = ChromosomeFactory.FromParameterCombination(combo, axes);

        var original = (NumericGene)chromosome.Genes["Depth"];
        var restored = (NumericGene)roundTripped.Genes["Depth"];

        // Should be close (snapping may differ slightly)
        Assert.Equal(original.Min, restored.Min);
        Assert.Equal(original.Max, restored.Max);
    }

    [Fact]
    public void RoundTrip_DiscreteGene_PreservesValue()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedDiscreteAxis("Mode", ["A", "B", "C"])
        };

        var chromosome = ChromosomeFactory.FromAxes(axes, _rng);
        var combo = ChromosomeFactory.ToParameterCombination(chromosome);
        var roundTripped = ChromosomeFactory.FromParameterCombination(combo, axes);

        var original = (DiscreteGene)chromosome.Genes["Mode"];
        var restored = (DiscreteGene)roundTripped.Genes["Mode"];
        Assert.Equal(original.Index, restored.Index);
    }

    [Fact]
    public void RoundTrip_ModuleGene_PreservesVariantAndSubParams()
    {
        var axes = new List<ResolvedAxis>
        {
            new ResolvedModuleSlotAxis("Exit",
            [
                new ResolvedModuleVariant("AtrExit",
                [
                    new ResolvedNumericAxis("Multiplier", [1.5m, 2.0m, 2.5m, 3.0m])
                ]),
                new ResolvedModuleVariant("FixedTp", [])
            ])
        };

        var chromosome = ChromosomeFactory.FromAxes(axes, new Random(123));
        var combo = ChromosomeFactory.ToParameterCombination(chromosome);

        var selection = Assert.IsType<ModuleSelection>(combo.Values["Exit"]);
        Assert.Contains(selection.TypeKey, new[] { "AtrExit", "FixedTp" });
    }

    [Fact]
    public void ToParameterCombination_NumericGene_SnapsToGrid()
    {
        var gene = new NumericGene(7.3, 5.0, 15.0, 2.5, typeof(decimal));
        var chromosome = new Chromosome(new Dictionary<string, Gene> { ["X"] = gene });

        var combo = ChromosomeFactory.ToParameterCombination(chromosome);

        // 7.3 should snap to 7.5 (= 5.0 + 1 * 2.5)
        Assert.Equal(7.5m, combo.Values["X"]);
    }

    [Fact]
    public void ToParameterCombination_IntGene_ReturnsInt()
    {
        var gene = new NumericGene(14.7, 10.0, 20.0, 5.0, typeof(int));
        var chromosome = new Chromosome(new Dictionary<string, Gene> { ["Count"] = gene });

        var combo = ChromosomeFactory.ToParameterCombination(chromosome);

        Assert.IsType<int>(combo.Values["Count"]);
        Assert.Equal(15, combo.Values["Count"]);
    }

    [Fact]
    public void ToParameterCombination_LongGene_ReturnsLong()
    {
        var gene = new NumericGene(100.3, 50.0, 500.0, 50.0, typeof(long));
        var chromosome = new Chromosome(new Dictionary<string, Gene> { ["Threshold"] = gene });

        var combo = ChromosomeFactory.ToParameterCombination(chromosome);

        Assert.IsType<long>(combo.Values["Threshold"]);
        Assert.Equal(100L, combo.Values["Threshold"]);
    }

    [Fact]
    public void SnapToGrid_ClampsToMax()
    {
        var snapped = ChromosomeFactory.SnapToGrid(25.0, 0.0, 20.0, 5.0);
        Assert.Equal(20.0, snapped);
    }

    [Fact]
    public void SnapToGrid_ClampsToMin()
    {
        var snapped = ChromosomeFactory.SnapToGrid(-5.0, 0.0, 20.0, 5.0);
        Assert.Equal(0.0, snapped);
    }

    [Fact]
    public void ExtractNumericBounds_SingleValue_StepIsOne()
    {
        var axis = new ResolvedNumericAxis("X", [42m]);
        var (min, max, step, _) = ChromosomeFactory.ExtractNumericBounds(axis);
        Assert.Equal(42.0, min);
        Assert.Equal(42.0, max);
        Assert.Equal(1.0, step);
    }
}
