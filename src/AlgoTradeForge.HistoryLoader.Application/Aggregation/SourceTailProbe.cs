using System.Globalization;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.HistoryLoader.Application.Aggregation;

/// <summary>
/// O(partition-tail) probe of the largest source-record <c>ts</c>. Used by the aggregate
/// endpoint's no_new_data check.
/// </summary>
public static class SourceTailProbe
{
    private const int TailReadBytes = 8 * 1024;

    /// <summary>Returns null if the source directory is missing or empty.</summary>
    public static long? GetLastTs(DataFeedDescriptor source)
    {
        var dir = source.Kind switch
        {
            DataFeedKind.TimeBar => Path.Combine(source.DataRoot, source.Exchange, source.Asset, "candles"),
            DataFeedKind.Tick => Path.Combine(source.DataRoot, source.Exchange, source.Asset, "ticks"),
            DataFeedKind.AltBar => Path.Combine(source.DataRoot, source.Exchange, source.Asset, "aggregated", source.FeedId),
            _ => throw new NotSupportedException(
                $"SourceTailProbe supports TimeBar, Tick, and AltBar; got Kind={source.Kind}."),
        };

        if (!Directory.Exists(dir)) return null;

        // Time-bar files carry a feed-id suffix; alt-bar and tick files don't.
        var pattern = source.Kind == DataFeedKind.TimeBar
            ? $"*_{source.FeedId}.csv"
            : "*.csv";

        var lastFile = Directory
            .EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .LastOrDefault();

        if (lastFile is null) return null;

        return GetLastTsFromFile(lastFile);
    }

    public static long? GetLastTsFromFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = fs.Length;
        if (length == 0) return null;

        var readSize = (int)Math.Min(length, TailReadBytes);
        var buffer = new byte[readSize];
        fs.Position = length - readSize;
        var read = fs.Read(buffer, 0, readSize);

        var end = read;
        while (end > 0 && (buffer[end - 1] == (byte)'\n' || buffer[end - 1] == (byte)'\r'))
            end--;

        var start = end;
        while (start > 0 && buffer[start - 1] != (byte)'\n')
            start--;

        if (start == end) return null;

        var line = System.Text.Encoding.UTF8.GetString(buffer, start, end - start);

        // Reject header lines (alphabetic) — guards against header-only files and rows
        // that exceed the tail read window.
        if (line.Length == 0 || !char.IsDigit(line[0])) return null;

        var commaIdx = line.IndexOf(',');
        var tsSlice = commaIdx < 0 ? line : line[..commaIdx];
        return long.TryParse(tsSlice, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)
            ? ts
            : null;
    }
}
