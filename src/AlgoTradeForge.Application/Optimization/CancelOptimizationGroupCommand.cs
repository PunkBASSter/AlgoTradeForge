using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.Application.Optimization;

public sealed record CancelOptimizationGroupCommand(Guid GroupId) : ICommand<bool>;

public sealed class CancelOptimizationGroupCommandHandler(
    IRunRepository repository,
    IRunCancellationRegistry cancellationRegistry) : ICommandHandler<CancelOptimizationGroupCommand, bool>
{
    public async Task<bool> HandleAsync(CancelOptimizationGroupCommand command, CancellationToken ct = default)
    {
        var group = await repository.GetOptimizationGroupByIdAsync(command.GroupId, ct);
        if (group is null)
            return false;

        // Cancel via group ID — the group-level CTS cascades to all linked per-DSS tokens
        cancellationRegistry.TryCancel(command.GroupId);

        return true;
    }
}
