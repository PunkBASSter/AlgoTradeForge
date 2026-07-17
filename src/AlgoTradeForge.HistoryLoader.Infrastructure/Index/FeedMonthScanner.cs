using AlgoTradeForge.HistoryLoader.Application.Index;
using AlgoTradeForge.HistoryLoader.Infrastructure.Archive;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Index;

public sealed class FeedMonthScanner : IFeedMonthScanner
{
    public async Task<IReadOnlyList<MonthPartitionRow>> Scan(
        string feedDir, string interval,
        IReadOnlyDictionary<string, MonthPartitionRow> known,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(interval) || !Directory.Exists(feedDir))
            return [];

        var result = new List<MonthPartitionRow>();
        foreach (var file in Directory.EnumerateFiles(feedDir, $"????-??_{interval}.csv"))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            var underscore = name.IndexOf('_');
            if (underscore != 7) continue;                 // strict "yyyy-MM_" prefix
            var month = name[..underscore];
            if (!name[(underscore + 1)..].Equals(interval, StringComparison.Ordinal)) continue;

            var fi = new FileInfo(file);
            var mtime = fi.LastWriteTimeUtc.ToString("O");
            if (known.TryGetValue(month, out var k) && k.FileLen == fi.Length && k.FileMtimeUtc == mtime)
            {
                result.Add(k);
                continue;
            }
            result.Add(new MonthPartitionRow(
                month, await ArchiveStatusMerger.CountDataRows(file, ct), fi.Length, mtime));
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Month, b.Month));
        return result;
    }
}
