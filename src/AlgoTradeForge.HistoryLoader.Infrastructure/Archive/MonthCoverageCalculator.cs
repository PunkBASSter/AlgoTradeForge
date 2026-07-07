using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class MonthCoverageCalculator : IMonthCoverageCalculator
{
    private readonly TimeProvider _clock;

    public MonthCoverageCalculator(TimeProvider clock) => _clock = clock;

    public async Task<bool> IsMonthCovered(
        string assetDir, string feedName, string interval,
        int year, int month,
        IReadOnlyList<DataGap> gaps,
        long? effectiveStartMs = null,
        CancellationToken ct = default)
    {
        var intervalMs = (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds;

        var monthStartMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var nextMonthDate = month == 12
            ? new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(year, month + 1, 1, 0, 0, 0, TimeSpan.Zero);
        var monthEndMs = nextMonthDate.ToUnixTimeMilliseconds();

        // Listing-month clamp: the pre-listing hole has no present row before it, so it is
        // unrecordable as a DataGap — the expectation starts at the feed's first data row.
        var effectiveStart = Math.Max(monthStartMs, effectiveStartMs ?? monthStartMs);

        var effectiveEndMs = Math.Min(monthEndMs, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        if (effectiveEndMs <= effectiveStart)
            return false;

        var expectedRows = (effectiveEndMs - effectiveStart) / intervalMs;

        // A month lying entirely inside one recorded gap has no partition file — correctly so.
        // Missing file means 0 actual rows; gap credit alone may still cover the month.
        var partitionPath = Path.Combine(assetDir, feedName, $"{year:D4}-{month:D2}_{interval}.csv");
        long actualRows = 0;
        if (File.Exists(partitionPath))
        {
            var lines = await File.ReadAllLinesAsync(partitionPath, ct);
            actualRows = Math.Max(0, lines.Length - 1);
        }

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
}
