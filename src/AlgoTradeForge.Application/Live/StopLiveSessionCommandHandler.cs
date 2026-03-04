using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Live;

public sealed class StopLiveSessionCommandHandler(
    ILiveSessionStore sessionStore) : ICommandHandler<StopLiveSessionCommand, bool>
{
    public async Task<bool> HandleAsync(StopLiveSessionCommand command, CancellationToken ct = default)
    {
        var entry = sessionStore.Get(command.SessionId);
        if (entry is null)
            return false;

        await entry.Connector.RemoveSessionAsync(command.SessionId, ct);
        sessionStore.Remove(command.SessionId);
        return true;
    }
}
