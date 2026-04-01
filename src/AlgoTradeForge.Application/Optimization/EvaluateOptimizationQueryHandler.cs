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
        // 1. Validate strategy exists
        var descriptor = spaceProvider.GetDescriptor(query.StrategyName)
            ?? throw new ArgumentException($"Strategy '{query.StrategyName}' not found.");

        // 2. Resolve parameter axes (pure computation, no scaling needed for counting)
        var resolvedAxes = axisResolver.Resolve(descriptor, query.Axes);

        // 3. Route subscriptions using the shared logic and build active axes
        var (_, axisDtos) = OptimizationSetupHelper.RouteSubscriptions(
            query.DataSubscriptions, query.SubscriptionAxis);
        var activeAxes = OptimizationSetupHelper.AppendSubscriptionAxisAndFilter(
            resolvedAxes, axisDtos.Count);

        // 4. Count combinations
        var totalCombinations = cartesianGenerator.EstimateCount(activeAxes);

        // 5. Compute unique combinations after normalization (if strategy supports it)
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

        // 6. Compute effective dimensions
        var effectiveDimensions = GeneticConfigResolver.ComputeEffectiveDimensions(activeAxes);

        // 7. Resolve genetic config if in genetic mode
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
            GeneticConfig = geneticConfigDto,
        };

        return Task.FromResult(dto);
    }
}
