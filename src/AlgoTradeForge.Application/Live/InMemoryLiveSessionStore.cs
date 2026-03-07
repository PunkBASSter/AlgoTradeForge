using System.Collections.Concurrent;
using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public sealed class InMemoryLiveSessionStore : ILiveSessionStore
{
    private readonly ConcurrentDictionary<Guid, SessionDetails> _sessions = new();
    private readonly ConcurrentDictionary<string, Guid> _fingerprints = new();

    public bool TryAdd(Guid sessionId, SessionDetails details)
    {
        if (!_fingerprints.TryAdd(details.Fingerprint, sessionId))
            return false;

        if (!_sessions.TryAdd(sessionId, details))
        {
            _fingerprints.TryRemove(details.Fingerprint, out _);
            return false;
        }

        return true;
    }

    public SessionDetails? Get(Guid sessionId) => _sessions.GetValueOrDefault(sessionId);

    public bool Remove(Guid sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var details))
            return false;

        _fingerprints.TryRemove(details.Fingerprint, out _);
        return true;
    }

    public IReadOnlyList<Guid> GetActiveSessionIds() => _sessions.Keys.ToList();
}
