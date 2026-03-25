namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// Per-gene-type mutation with adaptive rate:
/// <list type="bullet">
/// <item><b>Numeric</b>: Polynomial mutation (η_m=20) — perturbation proportional to range</item>
/// <item><b>Discrete</b>: Random replacement from allowed values</item>
/// <item><b>Module</b>: P=moduleVariantRate → switch variant; otherwise mutate sub-genes</item>
/// </list>
/// Adaptive rate: base rate scales up by <c>1 + stagnation * 0.1</c> (capped at 3×) when stuck.
/// </summary>
public static class MutationOperator
{
    public static void Mutate(
        Chromosome chromosome,
        double baseMutationRate,
        int stagnation,
        double polynomialEta,
        double moduleVariantRate,
        Random rng)
    {
        // Adaptive: increase mutation when stagnating (capped at 3× base)
        var adaptiveRate = baseMutationRate * Math.Min(1.0 + stagnation * 0.1, 3.0);

        var keys = new List<string>(chromosome.Genes.Keys);
        foreach (var key in keys)
        {
            if (rng.NextDouble() >= adaptiveRate)
                continue;

            chromosome.Genes[key] = MutateGene(
                chromosome.Genes[key], polynomialEta, moduleVariantRate, rng);
        }
    }

    private static Gene MutateGene(Gene gene, double eta, double moduleVariantRate, Random rng) =>
        gene switch
        {
            NumericGene n => PolynomialMutation(n, eta, rng),
            DiscreteGene d => RandomReplacement(d, rng),
            ModuleGene m => MutateModule(m, eta, moduleVariantRate, rng),
            _ => gene
        };

    /// <summary>
    /// Polynomial mutation: perturbation drawn from a polynomial distribution
    /// centered at the current value, bounded by [Min, Max].
    /// Matched to SBX crossover for consistent search behavior.
    /// </summary>
    private static NumericGene PolynomialMutation(NumericGene gene, double eta, Random rng)
    {
        var range = gene.Max - gene.Min;
        if (range <= 0) return gene;

        var u = rng.NextDouble();
        double delta;
        if (u < 0.5)
        {
            var bl = (gene.Value - gene.Min) / range;
            var b = 2.0 * u + (1.0 - 2.0 * u) * Math.Pow(1.0 - bl, eta + 1.0);
            delta = Math.Pow(b, 1.0 / (eta + 1.0)) - 1.0;
        }
        else
        {
            var bu = (gene.Max - gene.Value) / range;
            var b = 2.0 * (1.0 - u) + 2.0 * (u - 0.5) * Math.Pow(1.0 - bu, eta + 1.0);
            delta = 1.0 - Math.Pow(b, 1.0 / (eta + 1.0));
        }

        var mutated = gene.Value + delta * range;
        var snapped = ChromosomeFactory.SnapToGrid(mutated, gene.Min, gene.Max, gene.Step);
        return gene with { Value = snapped };
    }

    private static DiscreteGene RandomReplacement(DiscreteGene gene, Random rng)
    {
        if (gene.Choices.Count <= 1) return gene;
        var newIndex = rng.Next(gene.Choices.Count);
        return gene with { Index = newIndex };
    }

    private static ModuleGene MutateModule(ModuleGene gene, double eta, double moduleVariantRate, Random rng)
    {
        // With probability moduleVariantRate, switch to a different variant
        if (gene.Variants.Count > 1 && rng.NextDouble() < moduleVariantRate)
        {
            // Exclude current variant so mutation always produces a change
            var offset = rng.Next(gene.Variants.Count - 1);
            var newVariantIndex = offset >= gene.VariantIndex ? offset + 1 : offset;
            var newVariant = gene.Variants[newVariantIndex];

            // Fresh random sub-genes for the new variant
            Dictionary<string, Gene>? newSubGenes = null;
            if (newVariant.SubAxes.Count > 0)
            {
                newSubGenes = new Dictionary<string, Gene>(newVariant.SubAxes.Count);
                foreach (var subAxis in newVariant.SubAxes)
                    newSubGenes[subAxis.Name] = ChromosomeFactory.FromAxes([subAxis], rng).Genes.Values.First();
            }

            return gene with { VariantIndex = newVariantIndex, SubGenes = newSubGenes };
        }

        // Otherwise mutate existing sub-genes
        if (gene.SubGenes is null || gene.SubGenes.Count == 0)
            return gene;

        var mutatedSub = new Dictionary<string, Gene>(gene.SubGenes);
        foreach (var (name, subGene) in gene.SubGenes)
        {
            if (rng.NextDouble() < 0.5) // 50% chance to mutate each sub-gene
                mutatedSub[name] = MutateGene(subGene, eta, moduleVariantRate, rng);
        }

        return gene with { SubGenes = mutatedSub };
    }
}
