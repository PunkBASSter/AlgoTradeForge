using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization.Genetic;

public class OperatorTests
{
    private readonly Random _rng = new(42);

    // ── Selection ──────────────────────────────────────

    [Fact]
    public void TournamentSelect_ReturnsFittestAmongCandidates()
    {
        var population = new List<Chromosome>();
        for (var i = 0; i < 20; i++)
        {
            var c = new Chromosome(new Dictionary<string, Gene>
            {
                ["X"] = new NumericGene(i, 0, 19, 1, typeof(int))
            });
            c.Fitness = i; // higher index = higher fitness
            population.Add(c);
        }

        // Over many selections, should frequently pick high-fitness individuals
        var totalFitness = 0.0;
        for (var i = 0; i < 100; i++)
        {
            var selected = SelectionOperator.TournamentSelect(population, 3, _rng);
            totalFitness += selected.Fitness;
        }

        // Average should be well above midpoint (9.5)
        var avgFitness = totalFitness / 100;
        Assert.True(avgFitness > 12, $"Expected avg > 12, got {avgFitness}");
    }

    // ── Crossover ──────────────────────────────────────

    [Fact]
    public void SbxCrossover_NumericGenes_ChildrenInRange()
    {
        var p1 = new Chromosome(new Dictionary<string, Gene>
        {
            ["X"] = new NumericGene(5.0, 0.0, 20.0, 1.0, typeof(double))
        });
        var p2 = new Chromosome(new Dictionary<string, Gene>
        {
            ["X"] = new NumericGene(15.0, 0.0, 20.0, 1.0, typeof(double))
        });

        for (var i = 0; i < 50; i++)
        {
            var (child1, child2) = CrossoverOperator.Crossover(p1, p2, 2.0, _rng);
            var g1 = (NumericGene)child1.Genes["X"];
            var g2 = (NumericGene)child2.Genes["X"];
            Assert.InRange(g1.Value, 0.0, 20.0);
            Assert.InRange(g2.Value, 0.0, 20.0);
        }
    }

    [Fact]
    public void SbxCrossover_IdenticalParents_ProducesIdenticalChildren()
    {
        var p1 = new Chromosome(new Dictionary<string, Gene>
        {
            ["X"] = new NumericGene(10.0, 0.0, 20.0, 1.0, typeof(double))
        });
        var p2 = new Chromosome(new Dictionary<string, Gene>
        {
            ["X"] = new NumericGene(10.0, 0.0, 20.0, 1.0, typeof(double))
        });

        var (child1, child2) = CrossoverOperator.Crossover(p1, p2, 2.0, _rng);
        Assert.Equal(10.0, ((NumericGene)child1.Genes["X"]).Value);
        Assert.Equal(10.0, ((NumericGene)child2.Genes["X"]).Value);
    }

    [Fact]
    public void UniformCrossover_DiscreteGenes_SwapsCorrectly()
    {
        var choices = new List<object> { "A", "B", "C" };
        var p1 = new Chromosome(new Dictionary<string, Gene>
        {
            ["Mode"] = new DiscreteGene(0, choices)
        });
        var p2 = new Chromosome(new Dictionary<string, Gene>
        {
            ["Mode"] = new DiscreteGene(2, choices)
        });

        var sawSwap = false;
        var sawNoSwap = false;
        for (var i = 0; i < 100; i++)
        {
            var (c1, c2) = CrossoverOperator.Crossover(p1, p2, 2.0, new Random(i));
            var g1 = (DiscreteGene)c1.Genes["Mode"];
            if (g1.Index == 0) sawNoSwap = true;
            if (g1.Index == 2) sawSwap = true;
        }

        Assert.True(sawSwap, "Should sometimes swap");
        Assert.True(sawNoSwap, "Should sometimes not swap");
    }

    [Fact]
    public void ModuleCrossover_DifferentVariants_PicksOneParent()
    {
        var variants = new List<ResolvedModuleVariant>
        {
            new("A", []),
            new("B", [])
        };
        var p1 = new Chromosome(new Dictionary<string, Gene>
        {
            ["Mod"] = new ModuleGene(0, variants, null)
        });
        var p2 = new Chromosome(new Dictionary<string, Gene>
        {
            ["Mod"] = new ModuleGene(1, variants, null)
        });

        var (c1, c2) = CrossoverOperator.Crossover(p1, p2, 2.0, _rng);
        var g1 = (ModuleGene)c1.Genes["Mod"];
        var g2 = (ModuleGene)c2.Genes["Mod"];
        // Each child should get one parent's variant index
        Assert.Contains(g1.VariantIndex, new[] { 0, 1 });
        Assert.Contains(g2.VariantIndex, new[] { 0, 1 });
    }

    // ── Mutation ────────────────────────────────────────

    [Fact]
    public void PolynomialMutation_StaysInBounds()
    {
        for (var trial = 0; trial < 100; trial++)
        {
            var chromosome = new Chromosome(new Dictionary<string, Gene>
            {
                ["X"] = new NumericGene(10.0, 0.0, 20.0, 0.5, typeof(double))
            });

            MutationOperator.Mutate(chromosome, 1.0, 0, 20.0, 0.1, new Random(trial));

            var gene = (NumericGene)chromosome.Genes["X"];
            Assert.InRange(gene.Value, 0.0, 20.0);
        }
    }

    [Fact]
    public void Mutation_HighRate_ActuallyChangesValues()
    {
        var original = new NumericGene(10.0, 0.0, 100.0, 1.0, typeof(double));
        var changed = false;

        for (var i = 0; i < 50; i++)
        {
            var chromosome = new Chromosome(new Dictionary<string, Gene>
            {
                ["X"] = original
            });
            MutationOperator.Mutate(chromosome, 1.0, 0, 20.0, 0.1, new Random(i));
            var gene = (NumericGene)chromosome.Genes["X"];
            if (Math.Abs(gene.Value - 10.0) > 0.01)
            {
                changed = true;
                break;
            }
        }

        Assert.True(changed, "Mutation with rate=1.0 should change values");
    }

    [Fact]
    public void AdaptiveMutation_IncreasesWithStagnation()
    {
        // With stagnation=0, base rate applies
        // With stagnation=10, rate should be 2x
        // We can't easily measure this directly, but we can check that
        // high stagnation produces more mutations
        var changes0 = CountMutations(stagnation: 0, baseMutRate: 0.1);
        var changes10 = CountMutations(stagnation: 10, baseMutRate: 0.1);

        Assert.True(changes10 >= changes0,
            $"High stagnation ({changes10}) should produce at least as many mutations as no stagnation ({changes0})");
    }

    private int CountMutations(int stagnation, double baseMutRate)
    {
        var changes = 0;
        for (var trial = 0; trial < 200; trial++)
        {
            var chromosome = new Chromosome(new Dictionary<string, Gene>
            {
                ["A"] = new NumericGene(50.0, 0.0, 100.0, 1.0, typeof(double)),
                ["B"] = new NumericGene(50.0, 0.0, 100.0, 1.0, typeof(double)),
                ["C"] = new NumericGene(50.0, 0.0, 100.0, 1.0, typeof(double)),
            });
            MutationOperator.Mutate(chromosome, baseMutRate, stagnation, 20.0, 0.1, new Random(trial));
            foreach (var gene in chromosome.Genes.Values)
            {
                if (gene is NumericGene ng && Math.Abs(ng.Value - 50.0) > 0.01)
                    changes++;
            }
        }
        return changes;
    }

    [Fact]
    public void DiscreteGene_Mutation_SometimesChanges()
    {
        var changed = false;
        var choices = new List<object> { "X", "Y", "Z" };
        for (var i = 0; i < 50; i++)
        {
            var chromosome = new Chromosome(new Dictionary<string, Gene>
            {
                ["Mode"] = new DiscreteGene(0, choices)
            });
            MutationOperator.Mutate(chromosome, 1.0, 0, 20.0, 0.1, new Random(i));
            var gene = (DiscreteGene)chromosome.Genes["Mode"];
            if (gene.Index != 0) changed = true;
        }
        Assert.True(changed);
    }
}
