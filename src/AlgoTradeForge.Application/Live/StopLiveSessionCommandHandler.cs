using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public sealed class StopLiveSessionCommandHandler(
    ILiveSessionStore sessionStore,
    ILiveAccountManager accountManager) : ICommandHandler<StopLiveSessionCommand, bool>
{
    public async Task<bool> HandleAsync(StopLiveSessionCommand command, CancellationToken ct = default)
    {
        var entry = sessionStore.Get(command.SessionId);
        if (entry is null)
            return false;

        await entry.Connector.RemoveSessionAsync(command.SessionId, ct);
        sessionStore.Remove(command.SessionId);

        // Auto-dispose connector when last session is removed
        if (entry.Connector.SessionCount == 0)
            await accountManager.TryRemoveAsync(entry.AccountName, ct);

        return true;
    }
}
