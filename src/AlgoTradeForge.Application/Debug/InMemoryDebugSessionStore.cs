using System.Collections.Concurrent;

namespace AlgoTradeForge.Application.Debug;

public sealed class InMemoryDebugSessionStore : IDebugSessionStore
{
    public const int DefaultMaxSessions = 10;

    private readonly ConcurrentDictionary<Guid, DebugSession> _sessions = new();
    private readonly int _maxSessions;

    public InMemoryDebugSessionStore(int maxSessions = DefaultMaxSessions)
    {
        _maxSessions = maxSessions;
    }

    public DebugSession Create(string assetName, string strategyName)
    {
        if (_sessions.Count >= _maxSessions)
            throw new InvalidOperationException($"Maximum number of concurrent debug sessions ({_maxSessions}) reached.");

        var session = new DebugSession
        {
            AssetName = assetName,
            StrategyName = strategyName
        };
        _sessions[session.Id] = session;
        return session;
    }

    public DebugSession? Get(Guid sessionId) =>
        _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public bool TryRemove(Guid sessionId, out DebugSession? session) =>
        _sessions.TryRemove(sessionId, out session);

    public IReadOnlyList<DebugSession> GetAll() => [.. _sessions.Values];
}
