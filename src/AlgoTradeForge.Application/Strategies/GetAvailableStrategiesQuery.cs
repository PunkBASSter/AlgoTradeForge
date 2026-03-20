using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Strategies;

public sealed record GetAvailableStrategiesQuery : IQuery<IReadOnlyList<StrategyDescriptorDto>>;

public sealed class GetAvailableStrategiesQueryHandler(
    IOptimizationSpaceProvider provider) : IQueryHandler<GetAvailableStrategiesQuery, IReadOnlyList<StrategyDescriptorDto>>
{
    public Task<IReadOnlyList<StrategyDescriptorDto>> HandleAsync(
        GetAvailableStrategiesQuery query, CancellationToken ct = default)
    {
        var all = provider.GetAll();
        var result = all.Values
            .OrderBy(d => d.StrategyName)
            .Select(d =>
            {
                var defaults = provider.GetParameterDefaults(d);
                return new StrategyDescriptorDto(
                    d.StrategyName,
                    defaults,
                    d.Axes,
                    StrategyTemplateBuilder.BuildBacktestTemplate(d.StrategyName, defaults, d.Axes),
                    StrategyTemplateBuilder.BuildOptimizationTemplate(d.StrategyName, d.Axes),
                    StrategyTemplateBuilder.BuildLiveSessionTemplate(d.StrategyName, defaults, d.Axes),
                    StrategyTemplateBuilder.BuildDebugSessionTemplate(d.StrategyName, defaults, d.Axes));
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<StrategyDescriptorDto>>(result);
    }
}
