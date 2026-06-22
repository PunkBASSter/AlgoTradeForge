using AlgoTradeForge.Domain.Strategy;

namespace AlgoTradeForge.LiveHost.Application.Live.DataPlane;

// Identifies a bar kind within an instrument. Derived from a DataSubscription;
// the string value is the routing key for strategy dispatch.
public readonly record struct BarSpecKey(string Value)
{
    public static BarSpecKey TimeBar(TimeFrame tf) => new($"time:{tf}");
    public static BarSpecKey AltBar(string feedId) => new($"alt:{feedId}");
    public static readonly BarSpecKey RawTick = new("tick");
}
