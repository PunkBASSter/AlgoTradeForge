namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

internal static class IbSecTypeExtensions
{
    public static string ToIbString(this IbSecType type) => type switch
    {
        IbSecType.Stk => "STK",
        IbSecType.Fut => "FUT",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static IbSecType FromIbString(string raw) => raw switch
    {
        "STK" => IbSecType.Stk,
        "FUT" => IbSecType.Fut,
        _ => throw new ArgumentOutOfRangeException(nameof(raw), raw, "Unsupported IB security type."),
    };
}
