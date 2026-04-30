using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Loads <see cref="Int64Bar"/> series from partitioned CSV storage with per-<see cref="DataFeedKind"/>
/// path resolution (TRD §9.3, §9.5). Header on every supported file: <c>ts,o,h,l,c,vol</c>.
/// </summary>
/// <remarks>
/// Path / glob resolution by kind:
/// <list type="bullet">
///   <item><see cref="DataFeedKind.TimeBar"/> →
///         <c>{root}/{ex}/{asset}/candles/&lt;YYYY-MM&gt;_{FeedId}.csv</c>.
///         The per-FeedId suffix avoids the prior <c>*_*.csv</c> glob from picking up
///         <c>2026-04_5m.csv</c> while loading <c>1m</c> (P1a-30 regression).</item>
///   <item><see cref="DataFeedKind.AltBar"/> →
///         <c>{root}/{ex}/{asset}/aggregated/{FeedId}/*.csv</c>.
///         Lex sort matches chronological because part numbers are zero-padded
///         (<c>2026-04.csv</c> &lt; <c>2026-05.p01.csv</c> &lt; <c>2026-05.p02.csv</c>).</item>
///   <item><see cref="DataFeedKind.Tick"/> →
///         <c>{root}/{ex}/{asset}/ticks/*.csv</c>. Phase 2a fills this.</item>
///   <item><see cref="DataFeedKind.Side"/> →
///         <c>{root}/{ex}/{asset}/{FeedId}/*.csv</c>. Side feeds are normally read by
///         <see cref="CsvFeedSeriesLoader"/>; the path resolver covers the case for
///         completeness so a future caller doesn't crash.</item>
/// </list>
/// </remarks>
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

        // Last file by lex order = chronologically latest (zero-padded part numbers preserve ordering).
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

    // -------------------------------------------------------------------------
    // Path / glob resolution
    // -------------------------------------------------------------------------

    private static string ResolveFeedDir(DataFeedDescriptor feed) =>
        feed.Kind switch
        {
            DataFeedKind.TimeBar => Path.Combine(feed.DataRoot, feed.Exchange, feed.Asset, "candles"),
            DataFeedKind.AltBar  => Path.Combine(feed.DataRoot, feed.Exchange, feed.Asset, "aggregated", feed.FeedId),
            DataFeedKind.Tick    => Path.Combine(feed.DataRoot, feed.Exchange, feed.Asset, "ticks"),
            DataFeedKind.Side    => Path.Combine(feed.DataRoot, feed.Exchange, feed.Asset, feed.FeedId),
            _                    => throw new ArgumentOutOfRangeException(nameof(feed), $"Unsupported kind: {feed.Kind}"),
        };

    private static IEnumerable<string> EnumerateChronologicalFiles(DataFeedDescriptor feed, string dir)
    {
        // TimeBar uses a per-FeedId glob to avoid picking up sibling intervals — e.g. when
        // loading "1m", do NOT also pick up "2026-04_5m.csv" or "2026-04_1h.csv". Other kinds
        // use a permissive *.csv glob (alt bars live in their own subdir, ticks have no other
        // siblings, and Side is a single feed dir).
        var pattern = feed.Kind == DataFeedKind.TimeBar
            ? $"*_{feed.FeedId}.csv"
            : "*.csv";

        return Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);
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

    // The legacy `IntervalToString(TimeSpan)` private helper is replaced by
    // `AlgoTradeForge.Domain.Engine.TimeFrameFormatter.Format(TimeSpan)`. The conversion now
    // happens at the call site (CsvDataSource / HistoryRepository) when constructing
    // `DataFeedDescriptor.FeedId`; the loader itself only consumes the string id.
}
