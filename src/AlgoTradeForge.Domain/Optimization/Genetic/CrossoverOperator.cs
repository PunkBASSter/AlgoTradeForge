namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// Per-gene-type crossover:
/// <list type="bullet">
/// <item><b>Numeric</b>: SBX (Simulated Binary Crossover) — respects parent locality</item>
/// <item><b>Discrete</b>: Uniform crossover — randomly pick one parent's value</item>
/// <item><b>Module</b>: Same variant → crossover sub-genes; different variants → pick one parent's module</item>
/// </list>
/// </summary>
public static class CrossoverOperator
{
    public static (Chromosome Child1, Chromosome Child2) Crossover(
        Chromosome parent1, Chromosome parent2, double sbxEta, Random rng)
    {
        var genes1 = new Dictionary<string, Gene>(parent1.Genes.Count);
        var genes2 = new Dictionary<string, Gene>(parent2.Genes.Count);

        foreach (var (name, gene1) in parent1.Genes)
        {
            if (!parent2.Genes.TryGetValue(name, out var gene2))
            {
                genes1[name] = gene1;
                genes2[name] = gene1;
                continue;
            }

            var (child1Gene, child2Gene) = CrossoverGene(gene1, gene2, sbxEta, rng);
            genes1[name] = child1Gene;
            genes2[name] = child2Gene;
        }

        return (new Chromosome(genes1), new Chromosome(genes2));
    }

    private static (Gene, Gene) CrossoverGene(Gene g1, Gene g2, double eta, Random rng)
    {
        return (g1, g2) switch
        {
            (NumericGene n1, NumericGene n2) => SbxCrossover(n1, n2, eta, rng),
            (DiscreteGene d1, DiscreteGene d2) => UniformCrossover(d1, d2, rng),
            (ModuleGene m1, ModuleGene m2) => ModuleCrossover(m1, m2, eta, rng),
            _ => (g1, g2) // mismatched types — pass through
        };
    }

    /// <summary>
    /// Simulated Binary Crossover (SBX) for numeric genes.
    /// Produces two children whose spread around the parents is controlled by η.
    /// Higher η → children closer to parents.
    /// </summary>
    private static (Gene, Gene) SbxCrossover(NumericGene p1, NumericGene p2, double eta, Random rng)
    {
        if (Math.Abs(p1.Value - p2.Value) < 1e-14)
            return (p1, p2);

        var u = rng.NextDouble();
        double beta;
        if (u <= 0.5)
            beta = Math.Pow(2.0 * u, 1.0 / (eta + 1.0));
        else
            beta = Math.Pow(1.0 / (2.0 * (1.0 - u)), 1.0 / (eta + 1.0));

        var child1Val = 0.5 * ((1 + beta) * p1.Value + (1 - beta) * p2.Value);
        var child2Val = 0.5 * ((1 - beta) * p1.Value + (1 + beta) * p2.Value);

        child1Val = ChromosomeFactory.SnapToGrid(child1Val, p1.Min, p1.Max, p1.Step);
        child2Val = ChromosomeFactory.SnapToGrid(child2Val, p2.Min, p2.Max, p2.Step);

        return (
            p1 with { Value = child1Val },
            p2 with { Value = child2Val }
        );
    }

    private static (Gene, Gene) UniformCrossover(DiscreteGene d1, DiscreteGene d2, Random rng)
    {
        return rng.NextDouble() < 0.5 ? (d1, d2) : (d2, d1);
    }

    private static (Gene, Gene) ModuleCrossover(ModuleGene m1, ModuleGene m2, double eta, Random rng)
    {
        if (m1.VariantIndex != m2.VariantIndex)
        {
            // Different variants — pick one parent's entire module gene
            return rng.NextDouble() < 0.5 ? (m1, m2) : (m2, m1);
        }

        // Same variant — crossover sub-genes
        if (m1.SubGenes is null || m2.SubGenes is null)
            return (m1, m2);

        var sub1 = new Dictionary<string, Gene>(m1.SubGenes.Count);
        var sub2 = new Dictionary<string, Gene>(m2.SubGenes.Count);

        foreach (var (name, sg1) in m1.SubGenes)
        {
            if (m2.SubGenes.TryGetValue(name, out var sg2))
            {
                var (c1, c2) = CrossoverGene(sg1, sg2, eta, rng);
                sub1[name] = c1;
                sub2[name] = c2;
            }
            else
            {
                sub1[name] = sg1;
                sub2[name] = sg1;
            }
        }

        return (
            m1 with { SubGenes = sub1 },
            m2 with { SubGenes = sub2 }
        );
    }
}
