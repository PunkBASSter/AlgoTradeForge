namespace AlgoTradeForge.Live.Relay;

public enum SessionBoundaryReason : byte
{
    SessionStart = 1,
    SessionEnd = 2,
    ConnectorRestart = 3,
}
