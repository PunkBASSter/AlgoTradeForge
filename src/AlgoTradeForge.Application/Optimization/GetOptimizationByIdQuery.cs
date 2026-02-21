using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed record GetOptimizationByIdQuery(Guid Id) : ICommand<OptimizationRunRecord?>;

public sealed class GetOptimizationByIdQueryHandler(
    IRunRepository repository) : ICommandHandler<GetOptimizationByIdQuery, OptimizationRunRecord?>
{
    public Task<OptimizationRunRecord?> HandleAsync(GetOptimizationByIdQuery query, CancellationToken ct = default)
        => repository.GetOptimizationByIdAsync(query.Id, ct);
}
