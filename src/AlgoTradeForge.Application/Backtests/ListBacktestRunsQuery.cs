using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Backtests;

public sealed record ListBacktestRunsQuery(BacktestRunQuery Filter) : IQuery<PagedResult<BacktestRunRecord>>;

public sealed class ListBacktestRunsQueryHandler(
    IRunRepository repository) : IQueryHandler<ListBacktestRunsQuery, PagedResult<BacktestRunRecord>>
{
    public Task<PagedResult<BacktestRunRecord>> HandleAsync(ListBacktestRunsQuery query, CancellationToken ct = default)
        => repository.QueryAsync(query.Filter, ct);
}
