using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.Application.Optimization;

public sealed record CancelOptimizationGroupCommand(Guid GroupId) : ICommand<bool>;

public sealed class CancelOptimizationGroupCommandHandler(
    IRunRepository repository,
    ComputeTaskQueue queue,
    IRunCancellationRegistry cancellationRegistry) : ICommandHandler<CancelOptimizationGroupCommand, bool>
{
    public async Task<bool> HandleAsync(CancelOptimizationGroupCommand command, CancellationToken ct = default)
    {
        var group = await repository.GetOptimizationGroupByIdAsync(command.GroupId, ct);
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
