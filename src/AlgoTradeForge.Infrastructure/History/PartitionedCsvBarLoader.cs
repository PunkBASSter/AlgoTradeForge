using System.Text.RegularExpressions;
using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>Loads <see cref="Int64Bar"/> series from partitioned CSV storage. Header: <c>ts,o,h,l,c,vol</c>.</summary>
public sealed class PartitionedCsvBarLoader(IFileStorage storage) : IInt64BarLoader
{
    public async Task<TimeSeries<Int64Bar>> Load(DataFeedDescriptor feed, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var series = new TimeSeries<Int64Bar>();
        var dir = ResolveFeedDir(feed);
        var (keys, anyEntry) = await CollectChronologicalKeys(feed, dir, ct);
        if (!anyEntry)
            throw new DirectoryNotFoundException(
                $"Data feed directory not found or empty for {feed.Kind} feed '{feed.FeedId}' " +
                $"(asset={feed.Asset}, exchange={feed.Exchange}). Expected path: {dir}");

        var fromMs = new DateTimeOffset(from.Year, from.Month, from.Day, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to.Year, to.Month, to.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(1).ToUnixTimeMilliseconds() - 1;

        foreach (var key in keys)
        {
            var firstLine = true;
            await foreach (var line in storage.ReadLines(key, ct))
            {
                if (firstLine)
                {
                    firstLine = false;
                    continue;
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

    public async Task<DateTimeOffset?> GetLastTimestamp(DataFeedDescriptor feed, CancellationToken ct = default)
    {
        var dir = ResolveFeedDir(feed);
        var (keys, _) = await CollectChronologicalKeys(feed, dir, ct);
        if (keys.Count == 0)
            return null;

        var lastKey = keys[^1];
        var lastLine = await ReadLastDataLine(storage, lastKey, ct);
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

    // Single storage pass: tracks whether the directory yielded any entry at all (used to
    // distinguish "feed dir missing/empty" from "dir exists with sibling intervals only"),
    // while collecting just the keys that match this feed's filename shape.
    private async Task<(List<string> Keys, bool AnyEntry)> CollectChronologicalKeys(DataFeedDescriptor feed, string dir, CancellationToken ct)
    {
        var dirPrefix = WithTrailingSeparator(dir);
        var keys = new List<string>();
        var anyEntry = false;
        await foreach (var key in storage.ListKeys(dirPrefix, suffix: null, recursive: false, ct))
        {
            anyEntry = true;
            if (!key.EndsWith(".csv", StringComparison.Ordinal)) continue;

            var fileName = Path.GetFileName(key);
            if (feed.Kind == DataFeedKind.TimeBar && !MatchesTimeBarFeedId(fileName, feed.FeedId))
                continue;
            keys.Add(key);
        }

        keys.Sort(static (a, b) => string.CompareOrdinal(Path.GetFileName(a), Path.GetFileName(b)));
        EnsureNoDuplicateMonthPartitions(keys);
        return (keys, anyEntry);
    }

    // TimeBar partition filenames are "<YYYY-MM>_<feedId>.csv" or "<YYYY-MM>_<feedId>.pNN.csv".
    // Substring matching is too permissive ("_1m" collides with "_11m"; partial-suffix checks
    // can swallow unrelated ".part" files). Strip ".csv" and the optional ".pNN" tail, then
    // require the stem to terminate in exactly "_<feedId>".
    private static bool MatchesTimeBarFeedId(string fileName, string feedId)
    {
        var stem = fileName.AsSpan();
        if (!stem.EndsWith(".csv", StringComparison.Ordinal)) return false;
        stem = stem[..^4];

        var dotP = stem.LastIndexOf(".p", StringComparison.Ordinal);
        if (dotP > 0 && IsAllAsciiDigits(stem[(dotP + 2)..]))
            stem = stem[..dotP];

        if (stem.Length < feedId.Length + 1) return false;
        if (stem[^(feedId.Length + 1)] != '_') return false;
        return stem.EndsWith(feedId, StringComparison.Ordinal);
    }

    private static bool IsAllAsciiDigits(ReadOnlySpan<char> span)
    {
        if (span.Length == 0) return false;
        foreach (var c in span)
            if (!char.IsAsciiDigit(c)) return false;
        return true;
    }

    private static string WithTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith('/')
            ? path
            : path + Path.DirectorySeparatorChar;

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

    private static async Task<string?> ReadLastDataLine(IFileStorage storage, string key, CancellationToken ct)
    {
        string? lastLine = null;
        var firstLine = true;
        await foreach (var line in storage.ReadLines(key, ct))
        {
            if (firstLine) { firstLine = false; continue; }
            if (line.Length > 0)
                lastLine = line;
        }
        return lastLine;
    }
}
