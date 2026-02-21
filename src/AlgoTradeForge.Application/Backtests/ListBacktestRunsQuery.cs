using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Backtests;

public sealed record ListBacktestRunsQuery(BacktestRunQuery Filter) : ICommand<IReadOnlyList<BacktestRunRecord>>;

public sealed class ListBacktestRunsQueryHandler(
    IRunRepository repository) : ICommandHandler<ListBacktestRunsQuery, IReadOnlyList<BacktestRunRecord>>
{
    public Task<IReadOnlyList<BacktestRunRecord>> HandleAsync(ListBacktestRunsQuery query, CancellationToken ct = default)
        => repository.QueryAsync(query.Filter, ct);
}
