using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;
using Xunit;

namespace AlgoTradeForge.Domain.Tests.Optimization.Genetic;

public class GeneticAlgorithmTests
{
    private static List<ResolvedAxis> SimpleAxes => [
        new ResolvedNumericAxis("X", [1m, 2m, 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m]),
        new ResolvedNumericAxis("Y", [0.1m, 0.2m, 0.3m, 0.4m, 0.5m])
    ];

    private static GeneticConfig SimpleConfig => GeneticConfigResolver.Resolve(new GeneticConfig
    {
        PopulationSize = 20,
        MaxGenerations = 10,
        MaxEvaluations = 200,
        StagnationLimit = 5,
    }, SimpleAxes);

    [Fact]
    public void CreateInitialPopulation_CorrectSize()
    {
        var ga = new GeneticAlgorithm(SimpleConfig);
        var population = ga.CreateInitialPopulation(SimpleAxes, new Random(42));
        Assert.Equal(20, population.Count);
    }

    [Fact]
    public void CreateInitialPopulation_AllGenesPresent()
    {
        var ga = new GeneticAlgorithm(SimpleConfig);
        var population = ga.CreateInitialPopulation(SimpleAxes, new Random(42));

        foreach (var chromosome in population)
        {
            Assert.Contains("X", chromosome.Genes.Keys);
            Assert.Contains("Y", chromosome.Genes.Keys);
        }
    }

    [Fact]
    public void Evolve_ProducesCorrectPopulationSize()
    {
        var ga = new GeneticAlgorithm(SimpleConfig);
        var rng = new Random(42);
        var population = ga.CreateInitialPopulation(SimpleAxes, rng);

        // Assign fitness values
        for (var i = 0; i < population.Count; i++)
            population[i].Fitness = i;

        var nextGen = ga.Evolve(population, SimpleAxes, 0, 0, rng);

        Assert.Equal(20, nextGen.Count);
    }

    [Fact]
    public void Evolve_PreservesElites()
    {
        var config = GeneticConfigResolver.Resolve(new GeneticConfig
        {
            PopulationSize = 20,
            MaxGenerations = 10,
            MaxEvaluations = 200,
            EliteCount = 2,
        }, SimpleAxes);

        var ga = new GeneticAlgorithm(config);
        var rng = new Random(42);
        var population = ga.CreateInitialPopulation(SimpleAxes, rng);

        // Set fitness — best two have fitness 19 and 18
        for (var i = 0; i < population.Count; i++)
            population[i].Fitness = i;

        var nextGen = ga.Evolve(population, SimpleAxes, 0, 0, rng);

        Assert.Equal(20, nextGen.Count);

        // Elites should preserve fitness from the previous generation
        var eliteFitnesses = nextGen.Take(2).Select(c => c.Fitness).OrderDescending().ToList();
        Assert.Equal(19.0, eliteFitnesses[0]);
        Assert.Equal(18.0, eliteFitnesses[1]);

        // Elites are clones (different instances), not the same reference
        Assert.NotSame(population[^1], nextGen[0]);
        Assert.NotSame(population[^2], nextGen[1]);
    }

    [Fact]
    public void FullGA_ConvergesOnKnownOptimum()
    {
        // Fitness = -(X-7)^2 - (Y-0.3)^2 → optimum at X=7, Y=0.3
        var axes = SimpleAxes; // X in [1..10], Y in [0.1..0.5]
        var config = GeneticConfigResolver.Resolve(new GeneticConfig
        {
            PopulationSize = 30,
            MaxGenerations = 100,
            MaxEvaluations = 10_000,
            StagnationLimit = 30,
            EliteCount = 2,
        }, axes);

        var ga = new GeneticAlgorithm(config);
        var rng = new Random(42);
        var population = ga.CreateInitialPopulation(axes, rng);

        var bestFitness = double.MinValue;
        var stagnation = 0;

        for (var gen = 0; gen < config.MaxGenerations; gen++)
        {
            // Evaluate: fitness = -(x-7)^2 - (y-0.3)^2
            foreach (var c in population)
            {
                if (c.Fitness != double.MinValue) continue; // skip elites
                var x = ((NumericGene)c.Genes["X"]).Value;
                var y = ((NumericGene)c.Genes["Y"]).Value;
                c.Fitness = -Math.Pow(x - 7.0, 2) - Math.Pow(y - 0.3, 2);
            }

            var genBest = population.Max(c => c.Fitness);
            if (genBest > bestFitness)
            {
                bestFitness = genBest;
                stagnation = 0;
            }
            else
            {
                stagnation++;
            }

            if (ga.ShouldTerminate(gen + 1, (long)(gen + 1) * config.PopulationSize, stagnation, TimeSpan.Zero))
                break;

            population = ga.Evolve(population, axes, gen, stagnation, rng);
        }

        // Find the best individual
        var best = population.OrderByDescending(c => c.Fitness).First();
        var bestX = ((NumericGene)best.Genes["X"]).Value;
        var bestY = ((NumericGene)best.Genes["Y"]).Value;

        // Should converge within 2 steps of the optimum
        Assert.InRange(bestX, 5.0, 9.0); // optimal X=7, step=1
        Assert.InRange(bestY, 0.1, 0.5); // optimal Y=0.3, step=0.1
        Assert.True(bestFitness > -5.0, $"Expected fitness > -5, got {bestFitness}");
    }

    [Fact]
    public void ShouldTerminate_MaxGenerations_ReturnsTrue()
    {
        var config = new GeneticConfig
        {
            MaxGenerations = 10,
            MaxEvaluations = long.MaxValue,
            StagnationLimit = 100,
        };
        var ga = new GeneticAlgorithm(config);
        Assert.True(ga.ShouldTerminate(10, 50, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ShouldTerminate_MaxEvaluations_ReturnsTrue()
    {
        var config = new GeneticConfig
        {
            MaxGenerations = 100,
            MaxEvaluations = 100,
            StagnationLimit = 100,
        };
        var ga = new GeneticAlgorithm(config);
        Assert.True(ga.ShouldTerminate(5, 100, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ShouldTerminate_Stagnation_ReturnsTrue()
    {
        var config = new GeneticConfig
        {
            MaxGenerations = 100,
            MaxEvaluations = long.MaxValue,
            StagnationLimit = 5,
        };
        var ga = new GeneticAlgorithm(config);
        Assert.True(ga.ShouldTerminate(10, 50, 5, TimeSpan.Zero));
    }

    [Fact]
    public void ShouldTerminate_TimeBudget_ReturnsTrue()
    {
        var config = new GeneticConfig
        {
            MaxGenerations = 100,
            MaxEvaluations = long.MaxValue,
            StagnationLimit = 100,
            TimeBudget = TimeSpan.FromMinutes(5),
        };
        var ga = new GeneticAlgorithm(config);
        Assert.True(ga.ShouldTerminate(10, 50, 0, TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public void ShouldTerminate_NotYet_ReturnsFalse()
    {
        var config = new GeneticConfig
        {
            MaxGenerations = 100,
            MaxEvaluations = 10_000,
            StagnationLimit = 20,
            TimeBudget = TimeSpan.FromHours(1),
        };
        var ga = new GeneticAlgorithm(config);
        Assert.False(ga.ShouldTerminate(5, 50, 2, TimeSpan.FromMinutes(1)));
    }
}
