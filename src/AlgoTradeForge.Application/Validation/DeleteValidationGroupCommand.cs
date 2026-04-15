using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.Application.Validation;

public sealed record DeleteValidationGroupCommand(Guid GroupId) : ICommand<bool>;

public sealed class DeleteValidationGroupCommandHandler(
    IValidationRepository repository,
    IRunCancellationRegistry cancellationRegistry) : ICommandHandler<DeleteValidationGroupCommand, bool>
{
    public async Task<bool> HandleAsync(DeleteValidationGroupCommand command, CancellationToken ct = default)
    {
        var group = await repository.GetValidationGroupByIdAsync(command.GroupId);
        if (group is not null)
        {
            foreach (var run in group.Runs)
                cancellationRegistry.TryCancel(run.Id);
        }

        return await repository.DeleteValidationGroupAsync(command.GroupId);
    }
}
