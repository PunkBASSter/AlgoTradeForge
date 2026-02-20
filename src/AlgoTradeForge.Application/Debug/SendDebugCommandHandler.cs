using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Debug;

public sealed class SendDebugCommandHandler(IDebugSessionStore sessionStore)
    : ICommandHandler<SendDebugCommandRequest, DebugStepResultDto>
{
    public async Task<DebugStepResultDto> HandleAsync(SendDebugCommandRequest request, CancellationToken ct = default)
    {
        var session = sessionStore.Get(request.SessionId)
            ?? throw new ArgumentException($"Debug session '{request.SessionId}' not found.");

        var snapshot = await session.Probe.SendCommandAsync(request.Command, ct);

        return DebugStepResultDto.From(snapshot, session.Probe.IsRunning);
    }
}
