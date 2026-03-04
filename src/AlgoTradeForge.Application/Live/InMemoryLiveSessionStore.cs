using System.Collections.Concurrent;
using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public sealed class InMemoryLiveSessionStore : ILiveSessionStore
{
    private readonly ConcurrentDictionary<Guid, ILiveConnector> _sessions = new();

    public void Add(Guid sessionId, ILiveConnector connector) =>
        _sessions.TryAdd(sessionId, connector);

    public ILiveConnector? Get(Guid sessionId) =>
        _sessions.GetValueOrDefault(sessionId);

    public bool Remove(Guid sessionId) =>
        _sessions.TryRemove(sessionId, out _);

    public IReadOnlyList<Guid> GetActiveSessionIds() =>
        _sessions.Keys.ToList();
}
