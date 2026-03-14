using System.Collections.Concurrent;
using System.Globalization;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

internal sealed class FeedCsvWriter : IFeedWriter
{
    private readonly ConcurrentDictionary<string, long> _lastWrittenTimestamps = new();

    public void Write(
        string assetDir,
        string feedName,
        string interval,
        string[] columns,
        FeedRecord record)
    {
        var dedupKey = $"{assetDir}/{feedName}/{interval}";

        if (_lastWrittenTimestamps.TryGetValue(dedupKey, out var lastTs) && record.TimestampMs <= lastTs)
            return;

        var partitionDate = DateTimeOffset.FromUnixTimeMilliseconds(record.TimestampMs).UtcDateTime;
        var fileName = string.IsNullOrEmpty(interval)
            ? $"{partitionDate:yyyy-MM}.csv"
            : $"{partitionDate:yyyy-MM}_{interval}.csv";

        var feedDir = Path.Combine(assetDir, feedName);
        Directory.CreateDirectory(feedDir);

        var path = Path.Combine(feedDir, fileName);

        using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(fs);

        if (fs.Length == 0)
        {
            writer.WriteLine($"ts,{string.Join(',', columns)}");
        }

        var valuesPart = string.Join(',', record.Values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
        writer.WriteLine($"{record.TimestampMs},{valuesPart}");

        _lastWrittenTimestamps.AddOrUpdate(dedupKey, record.TimestampMs, (_, _) => record.TimestampMs);
    }

    public long? ResumeFrom(string assetDir, string feedName, string interval)
    {
        var feedDir = Path.Combine(assetDir, feedName);
        if (!Directory.Exists(feedDir))
            return null;

        var pattern = string.IsNullOrEmpty(interval) ? "????-??.csv" : $"????-??_{interval}.csv";
        var files = Directory.GetFiles(feedDir, pattern)
            .OrderByDescending(f => f)
            .ToArray();

        if (files.Length == 0)
            return null;

        var latestFile = files[0];

        string? lastLine = null;
        foreach (var line in File.ReadLines(latestFile))
        {
            if (!string.IsNullOrWhiteSpace(line))
                lastLine = line;
        }

        if (lastLine is null)
            return null;

        // Skip header line (starts with "ts")
        if (lastLine.StartsWith("ts", StringComparison.OrdinalIgnoreCase))
            return null;

        var firstComma = lastLine.IndexOf(',');
        if (firstComma < 0)
            return null;

        if (long.TryParse(lastLine[..firstComma], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
            return ts;

        return null;
    }
}
