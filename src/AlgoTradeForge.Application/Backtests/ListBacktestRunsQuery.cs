using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Backtests;

public sealed record ListBacktestRunsQuery(BacktestRunQuery Filter) : ICommand<PagedResult<BacktestRunRecord>>;

public sealed class ListBacktestRunsQueryHandler(
    IRunRepository repository) : ICommandHandler<ListBacktestRunsQuery, PagedResult<BacktestRunRecord>>
{
    public Task<PagedResult<BacktestRunRecord>> HandleAsync(ListBacktestRunsQuery query, CancellationToken ct = default)
        => repository.QueryAsync(query.Filter, ct);
}
