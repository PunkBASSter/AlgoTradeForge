using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public interface ILiveSessionStore
{
    void Add(Guid sessionId, ILiveConnector connector);
    ILiveConnector? Get(Guid sessionId);
    bool Remove(Guid sessionId);
    IReadOnlyList<Guid> GetActiveSessionIds();
}
