using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Live;

public sealed class StopLiveSessionCommandHandler(
    ILiveSessionStore sessionStore) : ICommandHandler<StopLiveSessionCommand, bool>
{
    public async Task<bool> HandleAsync(StopLiveSessionCommand command, CancellationToken ct = default)
    {
        var connector = sessionStore.Get(command.SessionId);
        if (connector is null)
            return false;

        await connector.StopAsync(ct);
        sessionStore.Remove(command.SessionId);
        return true;
    }
}
