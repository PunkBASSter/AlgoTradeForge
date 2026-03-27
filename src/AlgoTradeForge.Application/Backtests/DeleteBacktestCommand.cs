using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Backtests;

public sealed record DeleteBacktestCommand(Guid Id) : ICommand<bool>;

public sealed class DeleteBacktestCommandHandler(
    IRunRepository repository) : ICommandHandler<DeleteBacktestCommand, bool>
{
    public Task<bool> HandleAsync(DeleteBacktestCommand command, CancellationToken ct = default)
        => repository.DeleteBacktestAsync(command.Id, ct);
}
