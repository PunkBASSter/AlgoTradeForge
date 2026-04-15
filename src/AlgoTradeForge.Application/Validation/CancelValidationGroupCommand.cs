using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Application.Persistence;
using AlgoTradeForge.Application.Progress;

namespace AlgoTradeForge.Application.Validation;

public sealed record CancelValidationGroupCommand(Guid GroupId) : ICommand<bool>;

public sealed class CancelValidationGroupCommandHandler(
    IValidationRepository repository,
    IRunCancellationRegistry cancellationRegistry) : ICommandHandler<CancelValidationGroupCommand, bool>
{
    public async Task<bool> HandleAsync(CancelValidationGroupCommand command, CancellationToken ct = default)
    {
        var group = await repository.GetValidationGroupByIdAsync(command.GroupId, ct);
        if (group is null)
            return false;

        // Cancel via group ID — the group-level CTS cascades to all linked per-run tokens
        cancellationRegistry.TryCancel(command.GroupId);

        return true;
    }
}
