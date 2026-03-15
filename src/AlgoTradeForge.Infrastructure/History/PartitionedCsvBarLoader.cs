using AlgoTradeForge.Application.CandleIngestion;
using AlgoTradeForge.Domain.History;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Loads candle bars from monthly-partitioned CSV files.
/// Path pattern: {dataRoot}/{exchange}/{symbol}/candles/{YYYY-MM}_{interval}.csv
/// Header: ts,o,h,l,c,vol (all longs)
/// </summary>
public sealed class PartitionedCsvBarLoader : IInt64BarLoader
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
        180 => "3m",
        300 => "5m",
        900 => "15m",
        1800 => "30m",
        3600 => "1h",
        7200 => "2h",
        14400 => "4h",
        21600 => "6h",
        28800 => "8h",
        43200 => "12h",
        86400 => "1d",
        604800 => "1w",
        _ => throw new ArgumentException($"Unsupported interval: {interval}")
    };
}
