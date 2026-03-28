using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// Stratified random population initialization. Divides each numeric range into
/// <c>populationSize</c> strata and samples one per stratum for better coverage
/// than pure random without the complexity of Latin Hypercube in mixed-type spaces.
/// </summary>
public static class PopulationInitializer
{
    public static List<Chromosome> Create(
        IReadOnlyList<ResolvedAxis> axes, int populationSize, Random rng)
    {
        var population = new List<Chromosome>(populationSize);
        for (var i = 0; i < populationSize; i++)
        {
            var genes = new Dictionary<string, Gene>(axes.Count);
            foreach (var axis in axes)
                genes[axis.Name] = CreateStratifiedGene(axis, i, populationSize, rng);
            population.Add(new Chromosome(genes));
        }
        return population;
    }

    private static Gene CreateStratifiedGene(
        ResolvedAxis axis, int index, int populationSize, Random rng) => axis switch
    {
        ResolvedNumericAxis n => CreateStratifiedNumeric(n, index, populationSize, rng),
        ResolvedDiscreteAxis d => CreateStratifiedDiscrete(d, index),
        ResolvedModuleSlotAxis m => CreateStratifiedModule(m, index, populationSize, rng),
        _ => ChromosomeFactory.FromAxes([axis], rng).Genes.Values.First()
    };

    private static NumericGene CreateStratifiedNumeric(
        ResolvedNumericAxis axis, int index, int populationSize, Random rng)
    {
        var (min, max, step, clrType) = ChromosomeFactory.ExtractNumericBounds(axis);
        var range = max - min;

        if (range <= 0 || populationSize <= 1)
        {
            var val = ChromosomeFactory.SnapToGrid(min + rng.NextDouble() * Math.Max(range, 0), min, max, step);
            return new NumericGene(val, min, max, step, clrType);
        }

        // Stratified: divide range into populationSize strata, sample within stratum
        var stratumSize = range / populationSize;
        var stratumStart = min + index * stratumSize;
        var rawValue = stratumStart + rng.NextDouble() * stratumSize;
        var snapped = ChromosomeFactory.SnapToGrid(rawValue, min, max, step);

        return new NumericGene(snapped, min, max, step, clrType);
    }

    private static DiscreteGene CreateStratifiedDiscrete(ResolvedDiscreteAxis axis, int index)
    {
        // Cycle evenly across choices
        var choiceIndex = index % axis.Values.Count;
        return new DiscreteGene(choiceIndex, axis.Values);
    }

    private static ModuleGene CreateStratifiedModule(
        ResolvedModuleSlotAxis axis, int index, int populationSize, Random rng)
    {
        // Cycle evenly across variants
        var variantIndex = index % axis.Variants.Count;
        var variant = axis.Variants[variantIndex];

        Dictionary<string, Gene>? subGenes = null;
        if (variant.SubAxes.Count > 0)
        {
            subGenes = new Dictionary<string, Gene>(variant.SubAxes.Count);
            foreach (var subAxis in variant.SubAxes)
                subGenes[subAxis.Name] = CreateStratifiedGene(subAxis, index, populationSize, rng);
        }

        return new ModuleGene(variantIndex, axis.Variants, subGenes);
    }
}
