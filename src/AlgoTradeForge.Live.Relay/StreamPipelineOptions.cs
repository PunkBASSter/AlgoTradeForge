namespace AlgoTradeForge.Live.Relay;

public sealed record StreamPipelineOptions
{
    public int ChannelCapacity { get; init; } = 1 << 16;
    public long MaxSegmentBytes { get; init; } = 64L * 1024 * 1024;
}
