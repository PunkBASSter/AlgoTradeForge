using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Domain.Optimization.Genetic;

/// <summary>
/// Auto-sizes population and evaluation budget based on search space dimensionality.
/// Only fills in zero-valued fields — explicit user overrides are preserved.
/// </summary>
public static class GeneticConfigResolver
{
    public static GeneticConfig Resolve(GeneticConfig config, IReadOnlyList<ResolvedAxis> axes)
    {
        var effectiveDims = ComputeEffectiveDimensions(axes);
        var popSize = config.PopulationSize;
        var maxEvals = config.MaxEvaluations;
        var maxGens = config.MaxGenerations;
        var mutationRate = config.MutationRate;

        if (popSize <= 0)
            popSize = Math.Clamp(10 * (int)Math.Ceiling(Math.Sqrt(effectiveDims)), 50, 500);

        if (maxEvals <= 0)
            maxEvals = (long)popSize * 200;

        if (maxGens <= 0)
            maxGens = (int)(maxEvals / popSize);

        if (mutationRate < 0)
            mutationRate = effectiveDims > 0 ? 1.0 / effectiveDims : 0.1;

        return config with
        {
            PopulationSize = popSize,
            MaxEvaluations = maxEvals,
            MaxGenerations = maxGens,
            MutationRate = mutationRate,
        };
    }

    public static int ComputeEffectiveDimensions(IReadOnlyList<ResolvedAxis> axes)
    {
        var dims = 0;
        foreach (var axis in axes)
        {
            dims += axis switch
            {
                ResolvedNumericAxis => 1,
                ResolvedDiscreteAxis => 1,
                ResolvedModuleSlotAxis m => ComputeModuleDimensions(m),
                _ => 1
            };
        }
        return Math.Max(dims, 1);
    }

    private static int ComputeModuleDimensions(ResolvedModuleSlotAxis module)
    {
        // variant selection + max sub-axes across all variants
        var maxSubDims = 0;
        foreach (var variant in module.Variants)
        {
            var subDims = ComputeEffectiveDimensions(variant.SubAxes);
            if (subDims > maxSubDims) maxSubDims = subDims;
        }
        return 1 + maxSubDims;
    }
}
