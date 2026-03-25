using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// Converts between <see cref="Chromosome"/> and <see cref="ParameterCombination"/>,
/// and creates random chromosomes from resolved axes.
/// </summary>
public static class ChromosomeFactory
{
    /// <summary>
    /// Creates a random chromosome respecting axis bounds, steps, and types.
    /// </summary>
    public static Chromosome FromAxes(IReadOnlyList<ResolvedAxis> axes, Random rng)
    {
        var genes = new Dictionary<string, Gene>(axes.Count);
        foreach (var axis in axes)
            genes[axis.Name] = CreateRandomGene(axis, rng);
        return new Chromosome(genes);
    }

    /// <summary>
    /// Materializes a chromosome into a <see cref="ParameterCombination"/>
    /// with values snapped to step grids and converted to correct CLR types.
    /// </summary>
    public static ParameterCombination ToParameterCombination(Chromosome chromosome)
    {
        var values = new Dictionary<string, object>(chromosome.Genes.Count);
        foreach (var (name, gene) in chromosome.Genes)
            values[name] = MaterializeGene(gene);
        return new ParameterCombination(values);
    }

    /// <summary>
    /// Reverse mapping: creates a chromosome from an existing parameter combination and axes.
    /// Useful for seeding the population with known good parameters.
    /// </summary>
    public static Chromosome FromParameterCombination(
        ParameterCombination combo, IReadOnlyList<ResolvedAxis> axes)
    {
        var genes = new Dictionary<string, Gene>(axes.Count);
        foreach (var axis in axes)
        {
            if (!combo.Values.TryGetValue(axis.Name, out var value))
                continue;
            genes[axis.Name] = CreateGeneFromValue(axis, value);
        }
        return new Chromosome(genes);
    }

    private static Gene CreateRandomGene(ResolvedAxis axis, Random rng) => axis switch
    {
        ResolvedNumericAxis n => CreateRandomNumericGene(n, rng),
        ResolvedDiscreteAxis d => new DiscreteGene(rng.Next(d.Values.Count), d.Values),
        ResolvedModuleSlotAxis m => CreateRandomModuleGene(m, rng),
        _ => throw new InvalidOperationException($"Unknown axis type: {axis.GetType().Name}")
    };

    private static NumericGene CreateRandomNumericGene(ResolvedNumericAxis axis, Random rng)
    {
        var (min, max, step, clrType) = ExtractNumericBounds(axis);
        var range = max - min;
        var rawValue = min + rng.NextDouble() * range;
        var snapped = SnapToGrid(rawValue, min, max, step);
        return new NumericGene(snapped, min, max, step, clrType);
    }

    private static ModuleGene CreateRandomModuleGene(ResolvedModuleSlotAxis axis, Random rng)
    {
        var variantIndex = rng.Next(axis.Variants.Count);
        var variant = axis.Variants[variantIndex];
        var subGenes = CreateSubGenes(variant, rng);
        return new ModuleGene(variantIndex, axis.Variants, subGenes);
    }

    private static Dictionary<string, Gene>? CreateSubGenes(ResolvedModuleVariant variant, Random rng)
    {
        if (variant.SubAxes.Count == 0)
            return null;

        var subGenes = new Dictionary<string, Gene>(variant.SubAxes.Count);
        foreach (var subAxis in variant.SubAxes)
            subGenes[subAxis.Name] = CreateRandomGene(subAxis, rng);
        return subGenes;
    }

    private static Gene CreateGeneFromValue(ResolvedAxis axis, object value) => axis switch
    {
        ResolvedNumericAxis n => CreateNumericGeneFromValue(n, value),
        ResolvedDiscreteAxis d => CreateDiscreteGeneFromValue(d, value),
        ResolvedModuleSlotAxis m when value is ModuleSelection ms => CreateModuleGeneFromValue(m, ms),
        _ => throw new InvalidOperationException($"Cannot create gene from axis {axis.GetType().Name}")
    };

    private static NumericGene CreateNumericGeneFromValue(ResolvedNumericAxis axis, object value)
    {
        var (min, max, step, clrType) = ExtractNumericBounds(axis);
        var doubleValue = Convert.ToDouble(value);
        return new NumericGene(doubleValue, min, max, step, clrType);
    }

    private static DiscreteGene CreateDiscreteGeneFromValue(ResolvedDiscreteAxis axis, object value)
    {
        for (var i = 0; i < axis.Values.Count; i++)
        {
            if (Equals(axis.Values[i], value))
                return new DiscreteGene(i, axis.Values);
        }
        // Not found — default to first
        return new DiscreteGene(0, axis.Values);
    }

    private static ModuleGene CreateModuleGeneFromValue(ResolvedModuleSlotAxis axis, ModuleSelection ms)
    {
        var variantIndex = 0;
        for (var i = 0; i < axis.Variants.Count; i++)
        {
            if (axis.Variants[i].TypeKey == ms.TypeKey)
            {
                variantIndex = i;
                break;
            }
        }

        var variant = axis.Variants[variantIndex];
        Dictionary<string, Gene>? subGenes = null;
        if (variant.SubAxes.Count > 0 && ms.Params.Count > 0)
        {
            subGenes = new Dictionary<string, Gene>(variant.SubAxes.Count);
            foreach (var subAxis in variant.SubAxes)
            {
                if (ms.Params.TryGetValue(subAxis.Name, out var subValue))
                    subGenes[subAxis.Name] = CreateGeneFromValue(subAxis, subValue);
            }
        }

        return new ModuleGene(variantIndex, axis.Variants, subGenes);
    }

    private static object MaterializeGene(Gene gene) => gene switch
    {
        NumericGene n => MaterializeNumeric(n),
        DiscreteGene d => d.Choices[Math.Clamp(d.Index, 0, d.Choices.Count - 1)],
        ModuleGene m => MaterializeModule(m),
        _ => throw new InvalidOperationException($"Unknown gene type: {gene.GetType().Name}")
    };

    private static object MaterializeNumeric(NumericGene gene)
    {
        var snapped = SnapToGrid(gene.Value, gene.Min, gene.Max, gene.Step);

        if (gene.ClrType == typeof(int))
            return (int)Math.Round(snapped);
        if (gene.ClrType == typeof(long))
            return (long)Math.Round(snapped);
        if (gene.ClrType == typeof(decimal))
            return (decimal)snapped;
        if (gene.ClrType == typeof(float))
            return (float)snapped;
        return snapped;
    }

    private static ModuleSelection MaterializeModule(ModuleGene gene)
    {
        var variant = gene.Variants[Math.Clamp(gene.VariantIndex, 0, gene.Variants.Count - 1)];
        var subParams = new Dictionary<string, object>();

        if (gene.SubGenes is not null)
        {
            foreach (var (name, subGene) in gene.SubGenes)
                subParams[name] = MaterializeGene(subGene);
        }

        return new ModuleSelection(variant.TypeKey, subParams);
    }

    /// <summary>
    /// Extracts numeric bounds from a <see cref="ResolvedNumericAxis"/> by inspecting its values.
    /// The axis stores pre-expanded values; we reconstruct min/max/step/type from them.
    /// </summary>
    internal static (double Min, double Max, double Step, Type ClrType) ExtractNumericBounds(ResolvedNumericAxis axis)
    {
        if (axis.Values.Count == 0)
            return (0, 0, 1, typeof(double));

        var first = axis.Values[0];
        var clrType = first.GetType();
        var min = Convert.ToDouble(axis.Values[0]);
        var max = Convert.ToDouble(axis.Values[^1]);
        var step = axis.Values.Count > 1
            ? Convert.ToDouble(axis.Values[1]) - min
            : 1.0;

        // Prevent degenerate step
        if (step <= 0) step = 1.0;

        return (min, max, step, clrType);
    }

    /// <summary>
    /// Snaps a value to the nearest step grid point within [min, max].
    /// </summary>
    internal static double SnapToGrid(double value, double min, double max, double step)
    {
        if (step <= 0) return Math.Clamp(value, min, max);
        var steps = Math.Round((value - min) / step);
        var snapped = min + steps * step;
        return Math.Clamp(snapped, min, max);
    }
}
