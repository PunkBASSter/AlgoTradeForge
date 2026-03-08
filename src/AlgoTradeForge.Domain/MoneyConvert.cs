using System.Runtime.CompilerServices;

namespace AlgoTradeForge.Domain;

public static class MoneyConvert
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ToLong(decimal value) =>
        (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
