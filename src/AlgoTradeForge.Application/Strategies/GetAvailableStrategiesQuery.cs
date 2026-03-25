using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Strategies;

public sealed record GetAvailableStrategiesQuery : IQuery<IReadOnlyList<StrategyDescriptorDto>>;

public sealed class GetAvailableStrategiesQueryHandler(
    IOptimizationSpaceProvider provider,
    IAvailableAssetsProvider assetsProvider) : IQueryHandler<GetAvailableStrategiesQuery, IReadOnlyList<StrategyDescriptorDto>>
{
    public Task<IReadOnlyList<StrategyDescriptorDto>> HandleAsync(
        GetAvailableStrategiesQuery query, CancellationToken ct = default)
    {
        var all = provider.GetAll();
        var availableAssets = assetsProvider.GetAvailableAssets();
        var result = all.Values
            .OrderBy(d => d.StrategyName)
            .Select(d =>
            {
                var defaults = provider.GetParameterDefaults(d);
                return new StrategyDescriptorDto(
                    d.StrategyName,
                    defaults,
                    d.Axes,
                    StrategyTemplateBuilder.BuildBacktestTemplate(d.StrategyName, defaults, d.Axes, availableAssets),
                    StrategyTemplateBuilder.BuildOptimizationTemplate(d.StrategyName, d.Axes, availableAssets),
                    StrategyTemplateBuilder.BuildLiveSessionTemplate(d.StrategyName, defaults, d.Axes, availableAssets),
                    StrategyTemplateBuilder.BuildDebugSessionTemplate(d.StrategyName, defaults, d.Axes, availableAssets),
                    StrategyTemplateBuilder.BuildGeneticOptimizationTemplate(d.StrategyName, d.Axes, availableAssets));
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<StrategyDescriptorDto>>(result);
    }
}
