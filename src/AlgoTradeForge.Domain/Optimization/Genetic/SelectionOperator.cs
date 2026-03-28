namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// Tournament selection: picks <c>tournamentSize</c> random individuals
/// and returns the fittest one. Provides good selection pressure while
/// maintaining diversity better than rank-proportional selection.
/// </summary>
public static class SelectionOperator
{
    public static Chromosome TournamentSelect(
        IReadOnlyList<Chromosome> population, int tournamentSize, Random rng)
    {
        var best = population[rng.Next(population.Count)];

        for (var i = 1; i < tournamentSize; i++)
        {
            var candidate = population[rng.Next(population.Count)];
            if (candidate.Fitness > best.Fitness)
                best = candidate;
        }

        return best;
    }
}
