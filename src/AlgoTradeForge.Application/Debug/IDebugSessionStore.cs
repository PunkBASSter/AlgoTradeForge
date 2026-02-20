namespace AlgoTradeForge.Application.Debug;

public interface IDebugSessionStore
{
    DebugSession Create(string assetName, string strategyName);
    DebugSession? Get(Guid sessionId);
    bool TryRemove(Guid sessionId, out DebugSession? session);
    IReadOnlyList<DebugSession> GetAll();
}
