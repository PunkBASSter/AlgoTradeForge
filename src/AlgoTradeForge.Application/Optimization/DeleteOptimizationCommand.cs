using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;

namespace AlgoTradeForge.Application.Optimization;

public sealed record DeleteOptimizationCommand(Guid Id) : ICommand<bool>;

public sealed class DeleteOptimizationCommandHandler(
    IRunRepository repository) : ICommandHandler<DeleteOptimizationCommand, bool>
{
    public Task<bool> HandleAsync(DeleteOptimizationCommand command, CancellationToken ct = default)
        => repository.DeleteOptimizationAsync(command.Id, ct);
}
