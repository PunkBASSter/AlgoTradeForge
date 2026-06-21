using AlgoTradeForge.Application.Abstractions;

namespace AlgoTradeForge.LiveHost.Application.Live;

public sealed record StopLiveSessionCommand(Guid SessionId) : ICommand<bool>;
