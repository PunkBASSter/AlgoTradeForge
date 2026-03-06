using AlgoTradeForge.Domain.Live;

namespace AlgoTradeForge.Application.Live;

public sealed record SessionDetails(
    string AccountName,
    ILiveConnector Connector,
    string StrategyName,
    string StrategyVersion,
    string Exchange,
    string AssetName,
    string Fingerprint,
    DateTimeOffset StartedAt);

public interface ILiveSessionStore
{
    /// <summary>
    /// Adds a session. Returns false if a session with the same fingerprint already exists.
    /// </summary>
    bool TryAdd(Guid sessionId, SessionDetails details);
    SessionDetails? Get(Guid sessionId);
    bool Remove(Guid sessionId);
    IReadOnlyList<Guid> GetActiveSessionIds();
}
