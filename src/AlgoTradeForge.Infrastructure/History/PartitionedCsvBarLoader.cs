using System.Text.RegularExpressions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>Loads <see cref="Int64Bar"/> series from partitioned CSV storage. Header: <c>ts,o,h,l,c,vol</c>.</summary>
public sealed class PartitionedCsvBarLoader : IInt64BarLoader
{
    public TimeSeries<Int64Bar> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to)
    {
        var series = new TimeSeries<Int64Bar>();
        var dir = ResolveFeedDir(feed);
        if (!Directory.Exists(dir))
            return series;

        var fromMs = new DateTimeOffset(from.Year, from.Month, from.Day, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to.Year, to.Month, to.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(1).ToUnixTimeMilliseconds() - 1;

        foreach (var filePath in EnumerateChronologicalFiles(feed, dir))
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(fs);

            string? line;
            var firstLine = true;
            while ((line = reader.ReadLine()) is not null)
            {
                if (firstLine)
                {
                    firstLine = false;
                    continue; // skip header
                }

                if (line.Length == 0)
                    continue;

                var parts = line.Split(',');
                if (parts.Length < 6)
                    continue;

                if (!long.TryParse(parts[0], out var ts) ||
                    !long.TryParse(parts[1], out var open) ||
                    !long.TryParse(parts[2], out var high) ||
                    !long.TryParse(parts[3], out var low) ||
                    !long.TryParse(parts[4], out var close) ||
                    !long.TryParse(parts[5], out var volume))
                    continue;

                if (ts < fromMs || ts > toMs)
                    continue;

                series.Add(new Int64Bar(ts, open, high, low, close, volume));
            }
        }

        return series;
    }

    public DateTimeOffset? GetLastTimestamp(DataFeedDescriptor feed)
    {
        var dir = ResolveFeedDir(feed);
        if (!Directory.Exists(dir))
            return null;

        // Lex order = chronological because part numbers are zero-padded.
        var lastFile = EnumerateChronologicalFiles(feed, dir).LastOrDefault();
        if (lastFile is null)
            return null;

        var lastLine = ReadLastDataLine(lastFile);
        if (lastLine is null)
            return null;

        var commaIndex = lastLine.IndexOf(',');
        if (commaIndex > 0 && long.TryParse(lastLine[..commaIndex], out var tsMs))
            return DateTimeOffset.FromUnixTimeMilliseconds(tsMs);

        return null;
    }

    private static string ResolveFeedDir(DataFeedDescriptor feed) =>
        feed.Kind switch
        {
            DataFeedKind.TimeBar => Path.Combine(feed.DataRoot, feed.Exchange, feed.Asset, "candles"),
            DataFeedKind.AltBar  => Path.Combine(feed.DataRoot, feed.Exchange, feed.Asset, "aggregated", feed.FeedId),
            DataFeedKind.Tick    => Path.Combine(feed.DataRoot, feed.Exchange, feed.Asset, "ticks"),
            // Side feeds: ".flow" sidecars live under aggregated/, others (funding-rate, candle-ext) at top level.
            DataFeedKind.Side    => feed.FeedId.EndsWith(".flow", StringComparison.Ordinal)
                ? Path.Combine(feed.DataRoot, feed.Exchange, feed.Asset, "aggregated", feed.FeedId)
                : Path.Combine(feed.DataRoot, feed.Exchange, feed.Asset, feed.FeedId),
            _                    => throw new ArgumentOutOfRangeException(nameof(feed), $"Unsupported kind: {feed.Kind}"),
        };

    private static IEnumerable<string> EnumerateChronologicalFiles(DataFeedDescriptor feed, string dir)
    {
        // TimeBar must use a per-FeedId glob to avoid picking up sibling intervals
        // (loading "1m" must not match "2026-04_5m.csv"). Other kinds isolate by subdir.
        var pattern = feed.Kind == DataFeedKind.TimeBar
            ? $"*_{feed.FeedId}.csv"
            : "*.csv";

        var files = Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        EnsureNoDuplicateMonthPartitions(files);
        return files;
    }

    private static readonly Regex AltBarOrSidecarPattern =
        new(@"^(?<month>\d{4}-\d{2})(?:\.p(?<part>\d{2}))?\.csv$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TimeBarPattern =
        new(@"^(?<month>\d{4}-\d{2})_[^.]+(?:\.p(?<part>\d{2}))?\.csv$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Reject the (bare && .pNN) co-existence for the same month. A successful writer atomically
    // renames bare→p01 before opening p02, so this state implies operator error or partial migration.
    private static void EnsureNoDuplicateMonthPartitions(IEnumerable<string> filePaths)
    {
        var byMonth = new Dictionary<string, (string? Bare, List<string> PartNumbered)>(StringComparer.Ordinal);
        foreach (var path in filePaths)
        {
            var fileName = Path.GetFileName(path);
            var m = AltBarOrSidecarPattern.Match(fileName);
            if (!m.Success) m = TimeBarPattern.Match(fileName);
            if (!m.Success) continue;

            var month = m.Groups["month"].Value;
            var isPart = m.Groups["part"].Success;
            if (!byMonth.TryGetValue(month, out var entry))
                entry = (null, new List<string>());
            if (isPart) entry.PartNumbered.Add(path);
            else entry = (path, entry.PartNumbered);
            byMonth[month] = entry;
        }

        var collisions = byMonth
            .Where(kvp => kvp.Value.Bare is not null && kvp.Value.PartNumbered.Count > 0)
            .ToList();
        if (collisions.Count == 0) return;

        var details = string.Join("; ", collisions.Select(kvp =>
            $"month {kvp.Key}: bare='{kvp.Value.Bare}' AND partNumbered=[{string.Join(", ", kvp.Value.PartNumbered)}]"));
        throw new InvalidDataException(
            $"Partition layout violation: bare <YYYY-MM>.csv and <YYYY-MM>.pNN.csv co-exist for the same month. Details: {details}");
    }

    private static string? ReadLastDataLine(string filePath)
    {
        string? lastLine = null;
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(fs);
        string? line;
        var firstLine = true;
        while ((line = reader.ReadLine()) is not null)
        {
            if (firstLine) { firstLine = false; continue; }
            if (line.Length > 0)
                lastLine = line;
        }
        return lastLine;
    }
}
