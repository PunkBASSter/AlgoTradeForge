using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization.Genetic;

public class ChromosomeTests
{
    [Fact]
    public void Clone_DeepNested_ModuleGene_MutationDoesNotAffectOriginal()
    {
        // Arrange — ModuleGene containing a nested ModuleGene with a NumericGene leaf
        var innerNumeric = new NumericGene(5.0, 1.0, 10.0, 1.0, typeof(int));
        var innerSubGenes = new Dictionary<string, Gene> { ["Leaf"] = innerNumeric };

        var nestedModule = new ModuleGene(
            0,
            [new ResolvedModuleVariant("Inner", [new ResolvedNumericAxis("Leaf", [1m, 2m])])],
            innerSubGenes);

        var outerSubGenes = new Dictionary<string, Gene> { ["Nested"] = nestedModule };

        var outerModule = new ModuleGene(
            0,
            [new ResolvedModuleVariant("Outer",
            [
                new ResolvedModuleSlotAxis("Nested",
                [
                    new ResolvedModuleVariant("Inner", [new ResolvedNumericAxis("Leaf", [1m, 2m])])
                ])
            ])],
            outerSubGenes);

        var original = new Chromosome(new Dictionary<string, Gene>
        {
            ["Module"] = outerModule
        });

        // Act — clone and mutate the inner leaf in the clone
        var clone = original.Clone();
        var clonedOuter = (ModuleGene)clone.Genes["Module"];
        var clonedNested = (ModuleGene)clonedOuter.SubGenes!["Nested"];

        // Replace the leaf gene in the clone's inner sub-genes
        var mutatedDict = new Dictionary<string, Gene>(clonedNested.SubGenes!);
        mutatedDict["Leaf"] = new NumericGene(99.0, 1.0, 10.0, 1.0, typeof(int));
        clone.Genes["Module"] = clonedOuter with
        {
            SubGenes = new Dictionary<string, Gene>
            {
                ["Nested"] = clonedNested with { SubGenes = mutatedDict }
            }
        };

        // Assert — original is unaffected
        var origOuter = (ModuleGene)original.Genes["Module"];
        var origNested = (ModuleGene)origOuter.SubGenes!["Nested"];
        var origLeaf = (NumericGene)origNested.SubGenes!["Leaf"];

        Assert.Equal(5.0, origLeaf.Value);
    }

    [Fact]
    public void Clone_ShallowGenes_AreIndependent()
    {
        var original = new Chromosome(new Dictionary<string, Gene>
        {
            ["A"] = new NumericGene(3.0, 1.0, 10.0, 1.0, typeof(int)),
            ["B"] = new DiscreteGene(0, ["x", "y"]),
        });

        var clone = original.Clone();

        // Mutate clone's top-level dict
        clone.Genes["A"] = new NumericGene(7.0, 1.0, 10.0, 1.0, typeof(int));

        // Original unchanged
        Assert.Equal(3.0, ((NumericGene)original.Genes["A"]).Value);
    }
}
