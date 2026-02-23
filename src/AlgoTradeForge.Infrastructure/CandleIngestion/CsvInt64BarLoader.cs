using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Application.IO;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Infrastructure.CandleIngestion;

public sealed class CsvInt64BarLoader(IFileStorage fileStorage) : IInt64BarLoader
{
    public TimeSeries<Int64Bar> Load(
        string dataRoot,
        string exchange,
        string symbol,
        int decimalDigits,
        DateOnly from,
        DateOnly to,
        TimeSpan interval)
    {
        var series = new TimeSeries<Int64Bar>();

        var current = new DateOnly(from.Year, from.Month, 1);
        var endMonth = new DateOnly(to.Year, to.Month, 1);

        while (current <= endMonth)
        {
            var filePath = GetPartitionPath(dataRoot, exchange, symbol, current);
            if (File.Exists(filePath))
            {
                foreach (var line in fileStorage.ReadLines(filePath))
                {
                    if (line.StartsWith("Timestamp", StringComparison.Ordinal))
                        continue;

                    var parts = line.Split(',');
                    if (parts.Length < 6)
                        continue;

                    var timestamp = DateTimeOffset.Parse(parts[0]);
                    var rowDate = DateOnly.FromDateTime(timestamp.UtcDateTime);

                    if (rowDate < from || rowDate > to)
                        continue;

                    var bar = new Int64Bar(
                        timestamp.ToUnixTimeMilliseconds(),
                        long.Parse(parts[1]),
                        long.Parse(parts[2]),
                        long.Parse(parts[3]),
                        long.Parse(parts[4]),
                        long.Parse(parts[5]));

                    series.Add(bar);
                }
            }

            current = current.AddMonths(1);
        }

        return series;
    }

    public DateTimeOffset? GetLastTimestamp(string dataRoot, string exchange, string symbol)
    {
        var basePath = Path.Combine(dataRoot, exchange, symbol);
        if (!Directory.Exists(basePath))
            return null;

        var yearDirs = Directory.GetDirectories(basePath)
            .OrderByDescending(d => Path.GetFileName(d))
            .ToList();

        foreach (var yearDir in yearDirs)
        {
            var files = Directory.GetFiles(yearDir, "*.csv")
                .OrderByDescending(f => Path.GetFileNameWithoutExtension(f))
                .ToList();

            foreach (var file in files)
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

    private static string GetPartitionPath(string dataRoot, string exchange, string symbol, DateOnly month)
    {
        return Path.Combine(dataRoot, exchange, symbol, month.Year.ToString(), $"{month:yyyy-MM}.csv");
    }

    private string? ReadLastDataLine(string filePath)
    {
        string? lastLine = null;
        foreach (var line in fileStorage.ReadLines(filePath))
        {
            if (!line.StartsWith("Timestamp", StringComparison.Ordinal) && line.Length > 0)
                lastLine = line;
        }
        return lastLine;
    }
}
