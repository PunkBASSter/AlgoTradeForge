using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Backtests;

public sealed record GetBacktestByIdQuery(Guid Id) : ICommand<BacktestRunRecord?>;

public sealed class GetBacktestByIdQueryHandler(
    IRunRepository repository) : ICommandHandler<GetBacktestByIdQuery, BacktestRunRecord?>
{
    public Task<BacktestRunRecord?> HandleAsync(GetBacktestByIdQuery query, CancellationToken ct = default)
        => repository.GetByIdAsync(query.Id, ct);
}
