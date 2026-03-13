using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Loads candle bars from the new flat monthly-partitioned CSV format.
/// Path pattern: {dataRoot}/{exchange}/{symbol}/candles/{YYYY-MM}_{interval}.csv
/// Header: ts,o,h,l,c,vol (all longs)
/// </summary>
public sealed class NewFormatBarLoader : IInt64BarLoader
{
    public TimeSeries<Int64Bar> Load(
        string dataRoot,
        string exchange,
        string symbol,
        DateOnly from,
        DateOnly to,
        TimeSpan interval)
    {
        var series = new TimeSeries<Int64Bar>();
        var intervalStr = IntervalToString(interval);

        var fromMs = new DateTimeOffset(from.Year, from.Month, from.Day, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to.Year, to.Month, to.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(1).ToUnixTimeMilliseconds() - 1;

        var current = new DateOnly(from.Year, from.Month, 1);
        var endMonth = new DateOnly(to.Year, to.Month, 1);

        while (current <= endMonth)
        {
            var filePath = GetPartitionPath(dataRoot, exchange, symbol, current, intervalStr);
            if (File.Exists(filePath))
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

                    var ts = long.Parse(parts[0]);

                    if (ts < fromMs || ts > toMs)
                        continue;

                    series.Add(new Int64Bar(
                        ts,
                        long.Parse(parts[1]),
                        long.Parse(parts[2]),
                        long.Parse(parts[3]),
                        long.Parse(parts[4]),
                        long.Parse(parts[5])));
                }
            }

            current = current.AddMonths(1);
        }

        return series;
    }

    public DateTimeOffset? GetLastTimestamp(string dataRoot, string exchange, string symbol)
    {
        var candlesDir = Path.Combine(dataRoot, exchange, symbol, "candles");
        if (!Directory.Exists(candlesDir))
            return null;

        var files = Directory.GetFiles(candlesDir, "*_*.csv")
            .OrderByDescending(f => Path.GetFileNameWithoutExtension(f))
            .ToList();

        foreach (var file in files)
        {
            var lastLine = ReadLastDataLine(file);
            if (lastLine is null)
                continue;

            var commaIndex = lastLine.IndexOf(',');
            if (commaIndex > 0 && long.TryParse(lastLine[..commaIndex], out var tsMs))
                return DateTimeOffset.FromUnixTimeMilliseconds(tsMs);
        }

        return null;
    }

    private static string GetPartitionPath(
        string dataRoot, string exchange, string symbol, DateOnly month, string intervalStr)
    {
        return Path.Combine(
            dataRoot, exchange, symbol, "candles",
            $"{month:yyyy-MM}_{intervalStr}.csv");
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

    internal static string IntervalToString(TimeSpan interval) => interval.TotalSeconds switch
    {
        60 => "1m",
        300 => "5m",
        900 => "15m",
        1800 => "30m",
        3600 => "1h",
        14400 => "4h",
        86400 => "1d",
        604800 => "1w",
        _ => $"{(int)interval.TotalMinutes}m"
    };
}
