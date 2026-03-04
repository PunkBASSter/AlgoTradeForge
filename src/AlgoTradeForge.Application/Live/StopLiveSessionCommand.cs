using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.Application.Live;

public sealed record StopLiveSessionCommand(Guid SessionId) : ICommand<bool>;
