using System.Collections.Concurrent;
using AlgoTradeForge.HistoryLoader.Application.Archive;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

internal sealed class MonthCoverageCalculator : IMonthCoverageCalculator
{
    private readonly TimeProvider _clock;

    // Row counts memoized per (path, length, mtime). Partitions are replaced atomically
    // (PartitionFileWriter) or appended (BufferedPartitionWriter) — both move length+mtime,
    // so a stale entry cannot survive a content change.
    // TODO: no eviction — entries for deleted partitions persist; cap or prune if catalog-scale uptime makes this matter.
    private readonly ConcurrentDictionary<string, (long Length, DateTime MtimeUtc, long Rows)> _rowCounts = new();

    public MonthCoverageCalculator(TimeProvider clock) => _clock = clock;

    public async Task<bool> IsMonthCovered(
        string assetDir, string feedName, string interval,
        int year, int month,
        IReadOnlyList<DataGap> gaps,
        IReadOnlyList<string>? completeMonths = null,
        long? effectiveStartMs = null,
        CancellationToken ct = default)
    {
        // A month lying entirely inside one recorded gap has no partition file — correctly so.
        // Missing file means 0 actual rows; gap credit alone may still cover the month.
        var partitionPath = Path.Combine(assetDir, feedName, $"{year:D4}-{month:D2}_{interval}.csv");
        long actualRows = 0;
        var fileInfo = new FileInfo(partitionPath);
        if (fileInfo.Exists)
            actualRows = await CountDataRows(fileInfo, ct);

        return MonthCoverageMath.IsCovered(
            feedName, interval, year, month, actualRows,
            gaps, completeMonths, effectiveStartMs,
            _clock.GetUtcNow().ToUnixTimeMilliseconds());
    }

    private async Task<long> CountDataRows(FileInfo file, CancellationToken ct)
    {
        if (_rowCounts.TryGetValue(file.FullName, out var cached)
            && cached.Length == file.Length && cached.MtimeUtc == file.LastWriteTimeUtc)
            return cached.Rows;

        long lines = 0;
        using var reader = new StreamReader(file.FullName);
        while (await reader.ReadLineAsync(ct) is not null)
            lines++;

        var rows = Math.Max(0, lines - 1);
        _rowCounts[file.FullName] = (file.Length, file.LastWriteTimeUtc, rows);
        return rows;
    }
}
