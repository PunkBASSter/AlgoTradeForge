using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Binance;

namespace AlgoTradeForge.HistoryLoader.Storage;

internal sealed class CandleCsvWriter
{
    private readonly Dictionary<string, long> _lastWrittenTimestamps = new();

    public void Write(string assetDir, string interval, KlineRecord record, int decimalDigits)
    {
        var key = $"{assetDir}/{interval}";

        if (_lastWrittenTimestamps.TryGetValue(key, out var lastTs) && record.TimestampMs <= lastTs)
            return;

        var partitionPath = GetPartitionPath(assetDir, interval, record.TimestampMs);
        var dir = Path.GetDirectoryName(partitionPath)!;
        Directory.CreateDirectory(dir);

        var isNew = !File.Exists(partitionPath);

        if (isNew && _lastWrittenTimestamps.ContainsKey(key) is false)
        {
            // no last-ts yet; nothing to resume from for this new file
        }
        else if (isNew is false && _lastWrittenTimestamps.ContainsKey(key) is false)
        {
            // file exists but we haven't loaded the dedup state yet — skip; caller should
            // use ResumeFrom to seed the initial timestamp before writing
        }

        var fs = new FileStream(partitionPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(fs);

        if (isNew)
            writer.WriteLine("ts,o,h,l,c,vol");

        var multiplier = (decimal)Math.Pow(10, decimalDigits);
        var open   = MoneyConvert.ToLong(record.Open   * multiplier);
        var high   = MoneyConvert.ToLong(record.High   * multiplier);
        var low    = MoneyConvert.ToLong(record.Low    * multiplier);
        var close  = MoneyConvert.ToLong(record.Close  * multiplier);
        var volume = MoneyConvert.ToLong(record.Volume * multiplier);

        writer.WriteLine($"{record.TimestampMs},{open},{high},{low},{close},{volume}");

        _lastWrittenTimestamps[key] = record.TimestampMs;
    }

    public long? ResumeFrom(string assetDir, string interval)
    {
        var candlesDir = Path.Combine(assetDir, "candles");
        if (!Directory.Exists(candlesDir))
            return null;

        var pattern = $"*_{interval}.csv";
        var files = Directory.GetFiles(candlesDir, pattern)
                             .OrderByDescending(Path.GetFileName)
                             .ToArray();

        if (files.Length == 0)
            return null;

        foreach (var file in files)
        {
            var lastLine = ReadLastDataLine(file);
            if (lastLine is null)
                continue;

            var commaIndex = lastLine.IndexOf(',');
            if (commaIndex <= 0)
                continue;

            if (!long.TryParse(lastLine[..commaIndex], out var ts))
                continue;

            var key = $"{assetDir}/{interval}";
            _lastWrittenTimestamps[key] = ts;
            return ts;
        }

        return null;
    }

    private static string GetPartitionPath(string assetDir, string interval, long timestampMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs);
        var partition = dt.UtcDateTime.ToString("yyyy-MM");
        return Path.Combine(assetDir, "candles", $"{partition}_{interval}.csv");
    }

    private static string? ReadLastDataLine(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? lastLine = null;
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("ts", StringComparison.Ordinal) && line.Length > 0)
                lastLine = line;
        }

        return lastLine;
    }
}
