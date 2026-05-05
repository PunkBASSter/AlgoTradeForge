using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Optimization;
using AlgoTradeForge.Domain.Optimization.Genetic;
using AlgoTradeForge.Domain.Optimization.Space;

namespace AlgoTradeForge.Application.Optimization;

public sealed class EvaluateOptimizationQueryHandler(
    IOptimizationSpaceProvider spaceProvider,
    OptimizationAxisResolver axisResolver,
    ICartesianProductGenerator cartesianGenerator)
    : IQueryHandler<EvaluateOptimizationQuery, OptimizationEvaluationDto>
{
    public Task<OptimizationEvaluationDto> HandleAsync(
        EvaluateOptimizationQuery query, CancellationToken ct = default)
    {
        var descriptor = spaceProvider.GetDescriptor(query.StrategyName)
            ?? throw new ArgumentException($"Strategy '{query.StrategyName}' not found.");

        var resolvedAxes = axisResolver.Resolve(descriptor, query.Axes);

        // Expand multi-primary DSSes so the cost preview matches the post-expansion child-run
        // count produced by the submission handler.
        var expandedAxis = OptimizationSetupHelper.ExpandMultiPrimary(query.SubscriptionAxis);

        // Per-DSS group mode excludes the subscription axis.
        var dssCount = expandedAxis.Count;
        var activeAxes = dssCount > 0
            ? OptimizationSetupHelper.FilterEmptyAxes(resolvedAxes)
            : OptimizationSetupHelper.AppendSubscriptionAxisAndFilter(resolvedAxes, 0);

        var totalCombinations = cartesianGenerator.EstimateCount(activeAxes);

        long? uniqueCombinations = null;
        var normalizer = NormalizingEnumerable.TryCreateNormalizer(descriptor.ParamsType);
        if (normalizer is not null && totalCombinations <= query.MaxCombinations)
        {
            var seen = new HashSet<string>();
            foreach (var combo in cartesianGenerator.Enumerate(activeAxes))
            {
                var normalized = normalizer.Normalize(combo);
                seen.Add(GeneticFitnessCache.BuildCacheKey(normalized));
            }
            uniqueCombinations = seen.Count;
        }

        var effectiveDimensions = GeneticConfigResolver.ComputeEffectiveDimensions(activeAxes);


        ResolvedGeneticConfigDto? geneticConfigDto = null;
        if (string.Equals(query.Mode, "Genetic", StringComparison.OrdinalIgnoreCase))
        {
            var geneticConfig = query.GeneticSettings ?? new GeneticConfig();
            var resolved = GeneticConfigResolver.Resolve(geneticConfig, activeAxes);
            geneticConfigDto = new ResolvedGeneticConfigDto
            {
                PopulationSize = resolved.PopulationSize,
                MaxGenerations = resolved.MaxGenerations,
                MaxEvaluations = resolved.MaxEvaluations,
                MutationRate = resolved.MutationRate,
            };
        }

        // Genetic mode has no combination limit — cost is governed by MaxEvaluations.
        // Only brute-force is gated by MaxCombinations.
        var isGenetic = geneticConfigDto is not null;
        var effectiveCount = uniqueCombinations ?? totalCombinations;

        var dto = new OptimizationEvaluationDto
        {
            TotalCombinations = totalCombinations,
            UniqueCombinations = uniqueCombinations,
            ExceedsMaxCombinations = !isGenetic && effectiveCount > query.MaxCombinations,
            MaxCombinations = query.MaxCombinations,
            EffectiveDimensions = effectiveDimensions,
            DssCount = dssCount,
            GeneticConfig = geneticConfigDto,
        };

        return Task.FromResult(dto);
    }
}
