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
            .Select(d => new StrategyDescriptorDto(
                d.StrategyName,
                provider.GetParameterDefaults(d),
                d.Axes))
            .ToList();
        return Task.FromResult<IReadOnlyList<StrategyDescriptorDto>>(result);
    }
}
