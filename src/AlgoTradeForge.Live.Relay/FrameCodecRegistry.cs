using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Live.Relay;

public static class FrameCodecRegistry
{
    public static IReadOnlyDictionary<string, IFrameCodec> Default { get; } = new Dictionary<string, IFrameCodec>
    {
        ["trades"]   = new FrameCodec<TradeTick>(),
        ["quotes"]   = new FrameCodec<QuoteTick>(),
        ["_session"] = new FrameCodec<SessionEvent>(),
    };

    public static IFrameCodec For(string streamName) =>
        Default.TryGetValue(streamName, out var c) ? c : throw new ArgumentException($"No codec for stream '{streamName}'.", nameof(streamName));
}
