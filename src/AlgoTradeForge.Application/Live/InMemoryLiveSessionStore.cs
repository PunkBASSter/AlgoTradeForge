using System.Collections.Concurrent;
using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public sealed class InMemoryLiveSessionStore : ILiveSessionStore
{
    private readonly ConcurrentDictionary<Guid, SessionDetails> _sessions = new();

    public void Add(Guid sessionId, string accountName, ILiveConnector connector) =>
        _sessions.TryAdd(sessionId, new SessionDetails(accountName, connector));

    public SessionDetails? Get(Guid sessionId) => _sessions.GetValueOrDefault(sessionId);

    public bool Remove(Guid sessionId) => _sessions.TryRemove(sessionId, out _);

    public IReadOnlyList<Guid> GetActiveSessionIds() => _sessions.Keys.ToList();
}
