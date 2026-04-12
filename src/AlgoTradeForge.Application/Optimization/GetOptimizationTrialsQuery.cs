using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed record GetOptimizationTrialsQuery(
    Guid OptimizationId, int Limit = 50, int Offset = 0, string? SortBy = null)
    : IQuery<PagedResult<BacktestRunRecord>>;

public sealed class GetOptimizationTrialsQueryHandler(
    IRunRepository repository) : IQueryHandler<GetOptimizationTrialsQuery, PagedResult<BacktestRunRecord>>
{
    public Task<PagedResult<BacktestRunRecord>> HandleAsync(GetOptimizationTrialsQuery query, CancellationToken ct = default)
        => repository.GetOptimizationTrialsAsync(query.OptimizationId, query.Limit, query.Offset, query.SortBy, ct);
}
