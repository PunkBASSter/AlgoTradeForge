namespace AlgoTradeForge.HistoryLoader.Infrastructure.Binance;

public static class TickSizeParser
{
    /// <summary>"0.01000000" → 2; "1" → 0; trailing zeros ignored.</summary>
    public static int FractionalDigits(string tickOrStepSize)
    {
        var dotIdx = tickOrStepSize.IndexOf('.');
        if (dotIdx < 0) return 0;
        var fraction = tickOrStepSize.AsSpan(dotIdx + 1);
        var lastNonZero = -1;
        for (var i = 0; i < fraction.Length; i++)
            if (fraction[i] != '0') lastNonZero = i;
        return lastNonZero + 1;
    }
}
