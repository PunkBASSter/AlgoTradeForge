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

        foreach (var run in group.Runs)
        {
            if (run.Status == OptimizationRunStatus.InProgress)
                cancellationRegistry.TryCancel(run.Id);
        }

        return true;
    }
}
