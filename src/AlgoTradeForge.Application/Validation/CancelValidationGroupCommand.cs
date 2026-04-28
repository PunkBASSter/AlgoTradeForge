using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;
using AlgoTradeForge.Application.Optimization;

namespace AlgoTradeForge.Application.Validation;

public sealed record CancelValidationGroupCommand(Guid GroupId) : ICommand<bool>;

public sealed class CancelValidationGroupCommandHandler(
    IValidationRepository repository,
    ComputeTaskQueue queue,
    IRunCancellationRegistry cancellationRegistry) : ICommandHandler<CancelValidationGroupCommand, bool>
{
    public async Task<bool> HandleAsync(CancelValidationGroupCommand command, CancellationToken ct = default)
    {
        var group = await repository.GetValidationGroupByIdAsync(command.GroupId, ct);
        if (group is null)
            return false;

        // Cancel all queued/running tasks for this group
        var cancelled = queue.TryCancelJob(command.GroupId);

        // Trigger CTS for any in-progress task so the executor stops promptly
        foreach (var task in cancelled)
            cancellationRegistry.TryCancel(task.RunId);

        return true;
    }
}
