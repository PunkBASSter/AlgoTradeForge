namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// A single individual in the GA population — a collection of named genes
/// with an assigned fitness score after evaluation.
/// </summary>
public sealed class Chromosome
{
    public Dictionary<string, Gene> Genes { get; }
    public double Fitness { get; set; } = double.MinValue;

    public Chromosome(Dictionary<string, Gene> genes)
    {
        Genes = genes;
    }

    public Chromosome Clone()
    {
        var cloned = new Dictionary<string, Gene>(Genes.Count);
        foreach (var (key, gene) in Genes)
            cloned[key] = CloneGene(gene);
        return new Chromosome(cloned);
    }

    private static Gene CloneGene(Gene gene) => gene switch
    {
        ModuleGene mg when mg.SubGenes is not null =>
            mg with { SubGenes = new Dictionary<string, Gene>(mg.SubGenes) },
        _ => gene // records are immutable, no deep copy needed for Numeric/Discrete
    };
}
