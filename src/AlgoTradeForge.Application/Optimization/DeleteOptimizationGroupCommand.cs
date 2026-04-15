using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed record DeleteOptimizationGroupCommand(Guid Id) : ICommand<bool>;

public sealed class DeleteOptimizationGroupCommandHandler(
    IRunRepository repository) : ICommandHandler<DeleteOptimizationGroupCommand, bool>
{
    public Task<bool> HandleAsync(DeleteOptimizationGroupCommand command, CancellationToken ct = default)
        => repository.DeleteOptimizationGroupAsync(command.Id, ct);
}
