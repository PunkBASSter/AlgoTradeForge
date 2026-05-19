using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Storage;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Loads auxiliary feed data from monthly-partitioned CSV files.
/// Path pattern: {dataRoot}/{exchange}/{assetDir}/{feedName}/{YYYY-MM}[_{interval}].csv
/// Header: ts,col1,col2,...  (ts is long unix ms, columns are doubles).
/// When <c>nullableColumns</c> is true, empty cells parse as NaN; otherwise they throw.
/// Malformed non-empty cells always throw.
/// </summary>
public sealed class CsvFeedSeriesLoader : IFeedSeriesLoader
{
    private readonly IFileStorage _storage;
    private readonly ILogger<CsvFeedSeriesLoader> _logger;

    public CsvFeedSeriesLoader(IFileStorage storage, ILogger<CsvFeedSeriesLoader>? logger = null)
    {
        _storage = storage;
        _logger = logger ?? NullLogger<CsvFeedSeriesLoader>.Instance;
    }

    public async Task<FeedSeries?> Load(
        string dataRoot,
        string exchange,
        string assetDir,
        string feedName,
        string interval,
        DateOnly from,
        DateOnly to,
        bool nullableColumns = false,
        CancellationToken ct = default)
    {
        var timestamps = new List<long>();
        List<double>[]? columnLists = null;

        var current = new DateOnly(from.Year, from.Month, 1);
        var endMonth = new DateOnly(to.Year, to.Month, 1);

        var fromMs = new DateTimeOffset(from.Year, from.Month, from.Day, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to.Year, to.Month, to.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(1).ToUnixTimeMilliseconds() - 1;

        while (current <= endMonth)
        {
            var filePath = GetPartitionPath(dataRoot, exchange, assetDir, feedName, current, interval);
            if (!await _storage.Exists(filePath, ct))
            {
                current = current.AddMonths(1);
                continue;
            }

            var rowIndex = -1;
            var firstLine = true;
            await foreach (var line in _storage.ReadLines(filePath, ct))
            {
                rowIndex++;
                if (firstLine)
                {
                    firstLine = false;
                    if (columnLists is null)
                    {
                        var headerParts = line.Split(',');
                        var colCount = headerParts.Length - 1; // exclude ts column
                        columnLists = new List<double>[colCount];
                        for (var i = 0; i < colCount; i++)
                            columnLists[i] = [];
                    }
                    continue;
                }

                if (line.Length == 0)
                    continue;

                var parts = line.Split(',');
                if (parts.Length < 2)
                    continue;

                if (!long.TryParse(parts[0], out var ts))
                    continue;

                if (ts < fromMs || ts > toMs)
                    continue;

                if (columnLists is null)
                {
                    var colCount = parts.Length - 1;
                    columnLists = new List<double>[colCount];
                    for (var i = 0; i < colCount; i++)
                        columnLists[i] = [];
                }

                var values = new double[columnLists.Length];
                for (var c = 0; c < columnLists.Length; c++)
                {
                    var valueIdx = c + 1;
                    string? raw = valueIdx < parts.Length ? parts[valueIdx] : null;

                    if (raw is null || raw.Length == 0)
                    {
                        if (nullableColumns)
                        {
                            values[c] = double.NaN;
                        }
                        else
                        {
                            throw new FormatException(
                                $"Empty/missing cell in feed '{feedName}', file '{filePath}', " +
                                $"row {rowIndex} (ts={ts}), column index {c}. " +
                                $"Set nullable_columns: true in feeds.json to allow empty → NaN.");
                        }
                    }
                    else if (double.TryParse(raw, CultureInfo.InvariantCulture, out var v))
                    {
                        values[c] = v;
                    }
                    else
                    {
                        throw new FormatException(
                            $"Malformed numeric cell '{raw}' in feed '{feedName}', file '{filePath}', " +
                            $"row {rowIndex} (ts={ts}), column index {c}.");
                    }
                }

                timestamps.Add(ts);
                for (var c = 0; c < columnLists.Length; c++)
                    columnLists[c].Add(values[c]);
            }

            current = current.AddMonths(1);
        }

        if (timestamps.Count == 0 || columnLists is null)
            return null;

        var tsArray = timestamps.ToArray();
        var columns = new double[columnLists.Length][];
        for (var i = 0; i < columnLists.Length; i++)
            columns[i] = columnLists[i].ToArray();

        return new FeedSeries(tsArray, columns);
    }

    private static string GetPartitionPath(
        string dataRoot, string exchange, string assetDir, string feedName,
        DateOnly month, string interval)
    {
        var fileName = string.IsNullOrEmpty(interval)
            ? $"{month:yyyy-MM}.csv"
            : $"{month:yyyy-MM}_{interval}.csv";

        return Path.Combine(dataRoot, exchange, assetDir, feedName, fileName);
    }
}
