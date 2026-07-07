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
        CancellationToken ct = default)
    {
        var intervalMs = (long)IntervalParser.ToTimeSpan(interval).TotalMilliseconds;

        var monthStartMs = new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var nextMonthDate = month == 12
            ? new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(year, month + 1, 1, 0, 0, 0, TimeSpan.Zero);
        var monthEndMs = nextMonthDate.ToUnixTimeMilliseconds();

        var effectiveEndMs = Math.Min(monthEndMs, _clock.GetUtcNow().ToUnixTimeMilliseconds());
        if (effectiveEndMs <= monthStartMs)
            return false;

        var expectedRows = (effectiveEndMs - monthStartMs) / intervalMs;

        var partitionPath = Path.Combine(assetDir, feedName, $"{year:D4}-{month:D2}_{interval}.csv");
        if (!File.Exists(partitionPath))
            return false;

        var lines = await File.ReadAllLinesAsync(partitionPath, ct);
        var actualRows = Math.Max(0, lines.Length - 1);

        long gapRows = 0;
        foreach (var gap in gaps)
        {
            var to = Math.Min(gap.ToMs, effectiveEndMs);
            var from = Math.Max(gap.FromMs, monthStartMs);
            // span/interval − 1 = rows strictly inside the gap (both ends are present rows)
            var credit = (to - from) / intervalMs - 1;
            if (credit > 0)
                gapRows += credit;
        }

        return actualRows + gapRows >= expectedRows;
    }
}
