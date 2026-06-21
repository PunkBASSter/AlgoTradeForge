namespace AlgoTradeForge.Domain.History;

public enum SessionEventKind : byte
{
    Heartbeat = 0,
    SessionStart = 1,
    SessionEnd = 2,
    ConnectorRestart = 3
}
