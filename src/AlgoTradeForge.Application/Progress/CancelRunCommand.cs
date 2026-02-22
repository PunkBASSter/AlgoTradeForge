using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Progress;

public sealed record CancelRunCommand(Guid Id) : ICommand<bool>;

public sealed class CancelRunCommandHandler(
    IRunCancellationRegistry cancellationRegistry) : ICommandHandler<CancelRunCommand, bool>
{
    public Task<bool> HandleAsync(CancelRunCommand command, CancellationToken ct = default)
        => Task.FromResult(cancellationRegistry.TryCancel(command.Id));
}
