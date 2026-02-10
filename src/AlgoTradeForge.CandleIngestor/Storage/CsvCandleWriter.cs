using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.CandleIngestor.Storage;

public sealed class CsvCandleWriter(string dataRoot) : IDisposable
{
    private readonly string _dataRoot = dataRoot;
    private StreamWriter? _currentWriter;
    private string? _currentPartitionPath;
    private DateTimeOffset? _lastWrittenTimestamp;

    public void WriteCandle(RawCandle candle, string exchange, string symbol, int decimalDigits)
    {
        var partitionPath = GetPartitionPath(exchange, symbol, candle.Timestamp);

        if (_lastWrittenTimestamp.HasValue && candle.Timestamp <= _lastWrittenTimestamp.Value)
            return;

        if (_currentPartitionPath != partitionPath)
        {
            _currentWriter?.Flush();
            _currentWriter?.Dispose();

            var dir = Path.GetDirectoryName(partitionPath)!;
            Directory.CreateDirectory(dir);

            var isNew = !File.Exists(partitionPath);
            var fs = new FileStream(partitionPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _currentWriter = new StreamWriter(fs);

            if (isNew)
                _currentWriter.WriteLine("Timestamp,Open,High,Low,Close,Volume");

            if (!isNew)
            {
                _lastWrittenTimestamp = ReadLastTimestampFromFile(partitionPath);
                if (_lastWrittenTimestamp.HasValue && candle.Timestamp <= _lastWrittenTimestamp.Value)
                    return;
            }

            _currentPartitionPath = partitionPath;
        }

        var multiplier = (decimal)Math.Pow(10, decimalDigits);
        var open = (long)Math.Round(candle.Open * multiplier, MidpointRounding.AwayFromZero);
        var high = (long)Math.Round(candle.High * multiplier, MidpointRounding.AwayFromZero);
        var low = (long)Math.Round(candle.Low * multiplier, MidpointRounding.AwayFromZero);
        var close = (long)Math.Round(candle.Close * multiplier, MidpointRounding.AwayFromZero);
        var volume = (long)Math.Round(candle.Volume * multiplier, MidpointRounding.AwayFromZero);

        _currentWriter!.WriteLine($"{candle.Timestamp:yyyy-MM-ddTHH:mm:ss+00:00},{open},{high},{low},{close},{volume}");
        _lastWrittenTimestamp = candle.Timestamp;
    }

    public void Flush()
    {
        _currentWriter?.Flush();
    }

    public DateTimeOffset? GetLastTimestamp(string exchange, string symbol)
    {
        var basePath = Path.Combine(_dataRoot, exchange, symbol);
        if (!Directory.Exists(basePath))
            return null;

        foreach (var yearDir in Directory.GetDirectories(basePath).OrderByDescending(Path.GetFileName))
        {
            foreach (var file in Directory.GetFiles(yearDir, "*.csv").OrderByDescending(Path.GetFileNameWithoutExtension))
            {
                var lastLine = ReadLastDataLine(file);
                if (lastLine is not null)
                {
                    var commaIndex = lastLine.IndexOf(',');
                    if (commaIndex > 0)
                        return DateTimeOffset.Parse(lastLine[..commaIndex]);
                }
            }
        }

        return null;
    }

    private static string? ReadLastDataLine(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? lastLine = null;
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("Timestamp", StringComparison.Ordinal) && line.Length > 0)
                lastLine = line;
        }
        return lastLine;
    }

    public void Dispose()
    {
        _currentWriter?.Flush();
        _currentWriter?.Dispose();
        _currentWriter = null;
    }

    private string GetPartitionPath(string exchange, string symbol, DateTimeOffset timestamp)
    {
        var year = timestamp.UtcDateTime.Year.ToString();
        var month = timestamp.UtcDateTime.ToString("yyyy-MM");
        return Path.Combine(_dataRoot, exchange, symbol, year, $"{month}.csv");
    }

    private static DateTimeOffset? ReadLastTimestampFromFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? lastLine = null;
        while (reader.ReadLine() is { } line)
        {
            if (!line.StartsWith("Timestamp", StringComparison.Ordinal) && line.Length > 0)
                lastLine = line;
        }

        if (lastLine is null)
            return null;

        var commaIndex = lastLine.IndexOf(',');
        return commaIndex > 0 ? DateTimeOffset.Parse(lastLine[..commaIndex]) : null;
    }
}
