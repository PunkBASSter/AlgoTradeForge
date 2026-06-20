namespace AlgoTradeForge.Live.Relay;

public sealed record TickRelayOptions
{
    public int ChannelCapacity { get; init; } = 1 << 16;
    public long MaxSegmentBytes { get; init; } = 64L * 1024 * 1024;
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(10);
}
