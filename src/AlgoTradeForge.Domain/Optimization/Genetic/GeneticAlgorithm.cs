using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// Pure domain GA orchestrator — no I/O, no parallelism.
/// Provides population initialization, evolution, and termination logic.
/// </summary>
public sealed class GeneticAlgorithm
{
    private readonly GeneticConfig _config;

    public GeneticAlgorithm(GeneticConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Creates the initial population using stratified random initialization.
    /// </summary>
    public List<Chromosome> CreateInitialPopulation(IReadOnlyList<ResolvedAxis> axes, Random rng)
    {
        return PopulationInitializer.Create(axes, _config.PopulationSize, rng);
    }

    /// <summary>
    /// Evolves a fully-evaluated population into the next generation.
    /// Population must have Fitness assigned on all chromosomes before calling.
    /// </summary>
    public List<Chromosome> Evolve(
        List<Chromosome> evaluatedPopulation,
        IReadOnlyList<ResolvedAxis> axes,
        int generation,
        int stagnation,
        Random rng)
    {
        // Sort by fitness descending
        evaluatedPopulation.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));

        var nextGen = new List<Chromosome>(_config.PopulationSize);

        // Elitism: copy top individuals unchanged
        var eliteCount = Math.Min(_config.EliteCount, evaluatedPopulation.Count);
        for (var i = 0; i < eliteCount; i++)
            nextGen.Add(evaluatedPopulation[i].Clone());

        // Fill remaining via tournament select → crossover → mutation
        while (nextGen.Count < _config.PopulationSize)
        {
            var parent1 = SelectionOperator.TournamentSelect(evaluatedPopulation, _config.TournamentSize, rng);
            var parent2 = SelectionOperator.TournamentSelect(evaluatedPopulation, _config.TournamentSize, rng);

            Chromosome child1, child2;
            if (rng.NextDouble() < _config.CrossoverRate)
            {
                (child1, child2) = CrossoverOperator.Crossover(parent1, parent2, _config.SbxEta, rng);
            }
            else
            {
                child1 = parent1.Clone();
                child2 = parent2.Clone();
            }

            MutationOperator.Mutate(child1, _config.MutationRate, stagnation,
                _config.PolynomialMutationEta, _config.ModuleVariantMutationRate, rng);
            MutationOperator.Mutate(child2, _config.MutationRate, stagnation,
                _config.PolynomialMutationEta, _config.ModuleVariantMutationRate, rng);

            nextGen.Add(child1);
            if (nextGen.Count < _config.PopulationSize)
                nextGen.Add(child2);
        }

        return nextGen;
    }

    /// <summary>
    /// Checks whether the GA should terminate.
    /// </summary>
    public bool ShouldTerminate(int generation, long totalEvals, int stagnation, TimeSpan elapsed)
    {
        if (generation >= _config.MaxGenerations)
            return true;

        if (totalEvals >= _config.MaxEvaluations)
            return true;

        if (stagnation >= _config.StagnationLimit)
            return true;

        if (_config.TimeBudget.HasValue && elapsed >= _config.TimeBudget.Value)
            return true;

        return false;
    }
}
