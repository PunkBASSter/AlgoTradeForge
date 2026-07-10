namespace AlgoTradeForge.HistoryLoader.Domain;

public static class MonthCoverageMath
{
    public static bool IsCovered(
        string feedName, string interval, int year, int month,
        long actualRows,
        IReadOnlyList<DataGap> gaps,
        IReadOnlyList<string>? completeMonths,
        long? effectiveStartMs,
        long nowMs)
    {
        if (FeedNames.UsesMonthlyCompleteness(feedName))
            return completeMonths?.Contains($"{year:D4}-{month:D2}") ?? false;

        var intervalMs = (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds;

        var monthStartMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var nextMonthDate = month == 12
            ? new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(year, month + 1, 1, 0, 0, 0, TimeSpan.Zero);
        var monthEndMs = nextMonthDate.ToUnixTimeMilliseconds();

        // Listing-month clamp: the pre-listing hole has no present row before it, so it is
        // unrecordable as a DataGap — the expectation starts at the feed's first data row.
        var effectiveStart = Math.Max(monthStartMs, effectiveStartMs ?? monthStartMs);

        var effectiveEndMs = Math.Min(monthEndMs, nowMs);
        if (effectiveEndMs <= effectiveStart)
            return false;

        var expectedRows = (effectiveEndMs - effectiveStart) / intervalMs;

        long gapRows = 0;
        foreach (var gap in gaps)
        {
            // Count missing slots strictly inside [effectiveStart, effectiveEndMs): the gap's
            // ends are present rows, so the first missing slot is FromMs + interval. When the
            // clamp clips (gap crosses the month edge), the slot AT the clamp boundary is
            // itself missing and must be counted — hence no blanket "− 1".
            var from = Math.Max(gap.FromMs + intervalMs, effectiveStart);
            var to = Math.Min(gap.ToMs, effectiveEndMs);
            var credit = (to - from) / intervalMs;
            if (credit > 0)
                gapRows += credit;
        }

        return actualRows + gapRows >= expectedRows;
    }

    /// <summary>FirstTimestamp clamp: returns firstTs only when it falls inside (year, month).</summary>
    public static long? ListingClamp(long? firstTs, int year, int month)
    {
        if (firstTs is not { } ts)
            return null;
        var mStartMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var mEndMs = month == 12
            ? new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds()
            : new DateTimeOffset(year, month + 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return ts >= mStartMs && ts < mEndMs ? ts : null;
    }
}
