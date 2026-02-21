using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed record ListOptimizationRunsQuery(OptimizationRunQuery Filter) : ICommand<IReadOnlyList<OptimizationRunRecord>>;

public sealed class ListOptimizationRunsQueryHandler(
    IRunRepository repository) : ICommandHandler<ListOptimizationRunsQuery, IReadOnlyList<OptimizationRunRecord>>
{
    public Task<IReadOnlyList<OptimizationRunRecord>> HandleAsync(ListOptimizationRunsQuery query, CancellationToken ct = default)
        => repository.QueryOptimizationsAsync(query.Filter, ct);
}
