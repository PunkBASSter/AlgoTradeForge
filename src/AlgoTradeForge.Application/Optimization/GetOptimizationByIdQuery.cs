using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed record GetOptimizationByIdQuery(Guid Id) : IQuery<OptimizationRunRecord?>;

public sealed class GetOptimizationByIdQueryHandler(
    IRunRepository repository) : IQueryHandler<GetOptimizationByIdQuery, OptimizationRunRecord?>
{
    public Task<OptimizationRunRecord?> HandleAsync(GetOptimizationByIdQuery query, CancellationToken ct = default)
        => repository.GetOptimizationByIdAsync(query.Id, ct);
}
