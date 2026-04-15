using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed record GetOptimizationGroupByIdQuery(Guid Id) : IQuery<OptimizationGroupRecord?>;

public sealed class GetOptimizationGroupByIdQueryHandler(
    IRunRepository repository) : IQueryHandler<GetOptimizationGroupByIdQuery, OptimizationGroupRecord?>
{
    public Task<OptimizationGroupRecord?> HandleAsync(GetOptimizationGroupByIdQuery query, CancellationToken ct = default)
        => repository.GetOptimizationGroupByIdAsync(query.Id, ct);
}
