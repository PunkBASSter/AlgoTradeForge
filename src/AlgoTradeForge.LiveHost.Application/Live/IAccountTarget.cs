using AlgoTradeForge.Domain.Strategy;
using AlgoTradeForge.Domain.Trading;

namespace AlgoTradeForge.LiveHost.Application.Live;

public interface IAccountTarget : IAsyncDisposable
{
    string AccountName { get; }
    IOrderContext OrderContextFor(Guid sessionId);
    Portfolio Portfolio { get; }
}
