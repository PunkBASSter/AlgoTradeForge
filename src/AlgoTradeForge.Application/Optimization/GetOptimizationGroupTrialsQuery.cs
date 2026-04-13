using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed record GetOptimizationGroupTrialsQuery(
    Guid GroupId, int Limit = 1000, int Offset = 0, string? SortBy = null)
    : IQuery<PagedResult<BacktestRunRecord>>;

public sealed class GetOptimizationGroupTrialsQueryHandler(
    IRunRepository repository) : IQueryHandler<GetOptimizationGroupTrialsQuery, PagedResult<BacktestRunRecord>>
{
    public Task<PagedResult<BacktestRunRecord>> HandleAsync(
        GetOptimizationGroupTrialsQuery query, CancellationToken ct = default)
        => repository.GetOptimizationGroupTrialsAsync(query.GroupId, query.Limit, query.Offset, query.SortBy, ct);
}
