namespace AlgoTradeForge.Live.Relay;

public enum FrameType : byte
{
    Tick = 1,
    Heartbeat = 2,
    SessionBoundary = 3,
}
