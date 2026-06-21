using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Canonicalization;

public static class CanonicalScale
{
    // Inverts the relay's power-of-ten scaling. decimal-exact, then widened to the
    // double the canonical CSV writers persist.
    public static double Unscale(long raw, sbyte exp)
    {
        decimal scale = Pow10(Math.Abs(exp));
        decimal value = exp >= 0 ? raw / scale : raw * scale;
        return (double)value;
    }

    public static double ToIsBuyerMaker(AggressorSide side) =>
        side == AggressorSide.Sell ? 1.0 : 0.0;

    private static decimal Pow10(int n)
    {
        decimal r = 1m;
        for (int i = 0; i < n; i++) r *= 10m;
        return r;
    }
}
