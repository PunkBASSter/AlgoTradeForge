using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Debug;

public sealed record SendDebugCommandRequest : ICommand<DebugStepResultDto>
{
    public required Guid SessionId { get; init; }
    public required DebugCommand Command { get; init; }
}
