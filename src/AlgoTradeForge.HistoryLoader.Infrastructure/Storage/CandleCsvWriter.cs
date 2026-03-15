using System.Collections.Concurrent;
using AlgoTradeForge.Domain;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

internal sealed class CandleCsvWriter(WriteLockManager writeLockManager) : ICandleWriter
{
    private readonly ConcurrentDictionary<string, long> _lastWrittenTimestamps = new();

    public void Write(string assetDir, string interval, CandleRecord record, int decimalDigits)
    {
        var key = $"{assetDir}/{interval}";
        var sem = writeLockManager.GetLock(key);

        sem.Wait();
        try
        {
            if (_lastWrittenTimestamps.TryGetValue(key, out var lastTs) && record.TimestampMs <= lastTs)
                return;

            var partitionPath = GetPartitionPath(assetDir, interval, record.TimestampMs);
            var dir = Path.GetDirectoryName(partitionPath)!;
            Directory.CreateDirectory(dir);

            using var fs = new FileStream(partitionPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(fs);

            if (fs.Length == 0)
                writer.WriteLine("ts,o,h,l,c,vol");

            // Scale by 10^decimalDigits so the reader (CsvInt64BarLoader) can reconstruct
            // the original decimal values using the same multiplier stored in feeds.json.
            var multiplier = (decimal)Math.Pow(10, decimalDigits);
            var open   = MoneyConvert.ToLong(record.Open   * multiplier);
            var high   = MoneyConvert.ToLong(record.High   * multiplier);
            var low    = MoneyConvert.ToLong(record.Low    * multiplier);
            var close  = MoneyConvert.ToLong(record.Close  * multiplier);
            var volume = MoneyConvert.ToLong(record.Volume * multiplier);

            writer.WriteLine($"{record.TimestampMs},{open},{high},{low},{close},{volume}");

            _lastWrittenTimestamps.AddOrUpdate(key, record.TimestampMs, (_, _) => record.TimestampMs);
        }
        finally
        {
            sem.Release();
        }
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
            _lastWrittenTimestamps.AddOrUpdate(key, ts, (_, _) => ts);
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
        if (fs.Length == 0)
            return null;

        // Read backward from end of file in chunks to find the last data line
        const int chunkSize = 512;
        var buffer = new byte[chunkSize];
        var tail = new List<byte>();

        long pos = fs.Length;
        while (pos > 0)
        {
            int toRead = (int)Math.Min(chunkSize, pos);
            pos -= toRead;
            fs.Position = pos;
            int bytesRead = fs.Read(buffer, 0, toRead);

            // Prepend to accumulated tail
            for (int i = bytesRead - 1; i >= 0; i--)
                tail.Insert(0, buffer[i]);

            // Check if we have at least one complete non-header data line
            var text = System.Text.Encoding.UTF8.GetString(tail.ToArray());
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // Walk from last line backward looking for a data line
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length > 0 && !line.StartsWith("ts", StringComparison.Ordinal))
                    return line;
            }
        }

        return null;
    }
}
