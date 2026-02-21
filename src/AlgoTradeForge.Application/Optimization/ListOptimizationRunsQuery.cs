using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed record ListOptimizationRunsQuery(OptimizationRunQuery Filter) : ICommand<PagedResult<OptimizationRunRecord>>;

public sealed class ListOptimizationRunsQueryHandler(
    IRunRepository repository) : ICommandHandler<ListOptimizationRunsQuery, PagedResult<OptimizationRunRecord>>
{
    public Task<PagedResult<OptimizationRunRecord>> HandleAsync(ListOptimizationRunsQuery query, CancellationToken ct = default)
        => repository.QueryOptimizationsAsync(query.Filter, ct);
}
