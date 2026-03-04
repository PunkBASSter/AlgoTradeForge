using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public sealed record SessionDetails(string AccountName, ILiveConnector Connector);

public interface ILiveSessionStore
{
    void Add(Guid sessionId, string accountName, ILiveConnector connector);
    SessionDetails? Get(Guid sessionId);
    bool Remove(Guid sessionId);
    IReadOnlyList<Guid> GetActiveSessionIds();
}
