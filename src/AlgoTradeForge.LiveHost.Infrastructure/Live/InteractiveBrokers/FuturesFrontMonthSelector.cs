using System.Globalization;

namespace AlgoTradeForge.LiveHost.Infrastructure.Live.InteractiveBrokers;

// Picks the front-month futures contract: the nearest expiry on or after today. Roll timing (switching to the
// next month a few days before expiry) is a trading concern deferred to Plan 3/4.
internal static class FuturesFrontMonthSelector
{
    public static IbContractDetailsResult SelectFrontMonth(
        IReadOnlyList<IbContractDetailsResult> candidates, DateOnly today)
    {
        if (candidates.Count == 0)
            throw new InvalidOperationException("No contract details returned for futures resolution.");

        IbContractDetailsResult? best = null;
        var bestDate = DateOnly.MaxValue;
        foreach (var candidate in candidates)
        {
            var expiry = ParseExpiry(candidate.LastTradeDate);
            if (expiry < today || expiry >= bestDate) continue;
            best = candidate;
            bestDate = expiry;
        }

        return best ?? throw new InvalidOperationException("All returned futures contracts are expired.");
    }

    // IB LastTradeDateOrContractMonth is "yyyymmdd" or "yyyymm".
    private static DateOnly ParseExpiry(string raw) => raw.Length switch
    {
        8 => DateOnly.ParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture),
        6 => DateOnly.ParseExact(raw + "01", "yyyyMMdd", CultureInfo.InvariantCulture),
        _ => throw new FormatException($"Unrecognized IB expiry format: '{raw}'."),
    };
}
