using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Debug;

public sealed class SendDebugCommandHandler(IDebugSessionStore sessionStore)
    : ICommandHandler<SendDebugCommandRequest, DebugStepResultDto>
{
    public async Task<DebugStepResultDto> HandleAsync(SendDebugCommandRequest request, CancellationToken ct = default)
    {
        var session = sessionStore.Get(request.SessionId)
            ?? throw new ArgumentException($"Debug session '{request.SessionId}' not found.");

        // SetExport is a config command — apply it to the EventBus directly
        // rather than forwarding to the probe (which only handles stepping commands).
        if (request.Command is DebugCommand.SetExport setExport)
        {
            session.EventBus?.SetMutationsEnabled(setExport.Mutations);
            return DebugStepResultDto.From(session.Probe.LastSnapshot, session.Probe.IsRunning);
        }

        var snapshot = await session.Probe.SendCommandAsync(request.Command, ct);

        return DebugStepResultDto.From(snapshot, session.Probe.IsRunning);
    }
}
