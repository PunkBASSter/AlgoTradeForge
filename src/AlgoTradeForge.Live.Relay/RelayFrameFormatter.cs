using System.Globalization;

namespace AlgoTradeForge.Live.Relay;

public static class RelayFrameFormatter
{
    public static string Format(in RelayFrame frame)
    {
        var ci = CultureInfo.InvariantCulture;
        return frame.Type switch
        {
            FrameType.Tick =>
                $"TICK ts={frame.TimestampMs.ToString(ci)} " +
                $"price={frame.Trade.Price.ToString(ci)} " +
                $"qty={frame.Trade.Quantity.ToString(ci)} " +
                $"seq={frame.Trade.Sequence.ToString(ci)} " +
                $"aggressor={frame.Trade.Aggressor}",
            FrameType.Heartbeat =>
                $"HEARTBEAT ts={frame.TimestampMs.ToString(ci)}",
            FrameType.SessionBoundary =>
                $"BOUNDARY ts={frame.TimestampMs.ToString(ci)} reason={(SessionBoundaryReason)frame.ReasonCode}",
            _ => $"UNKNOWN type={(byte)frame.Type}",
        };
    }
}
