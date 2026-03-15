using System.Globalization;
using AlgoTradeForge.Application.Abstractions;
using AlgoTradeForge.Domain.History;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlgoTradeForge.Infrastructure.History;

/// <summary>
/// Loads auxiliary feed data from monthly-partitioned CSV files.
/// Path pattern: {dataRoot}/{exchange}/{assetDir}/{feedName}/{YYYY-MM}[_{interval}].csv
/// Header: ts,col1,col2,...  (ts is long unix ms, columns are doubles)
/// </summary>
public sealed class CsvFeedSeriesLoader : IFeedSeriesLoader
{
    private readonly ILogger<CsvFeedSeriesLoader> _logger;

    public CsvFeedSeriesLoader(ILogger<CsvFeedSeriesLoader>? logger = null)
    {
        _logger = logger ?? NullLogger<CsvFeedSeriesLoader>.Instance;
    }

    /// <summary>
    /// Loads feed data for the given date range.
    /// </summary>
    /// <param name="dataRoot">Root data directory.</param>
    /// <param name="exchange">Exchange name.</param>
    /// <param name="assetDir">Asset directory name (e.g. BTCUSDT or BTCUSDT_fut).</param>
    /// <param name="feedName">Feed subdirectory name (e.g. "funding_rate").</param>
    /// <param name="interval">Optional interval suffix for the filename (e.g. "1h"). Empty string means no suffix.</param>
    /// <param name="from">Start date (inclusive).</param>
    /// <param name="to">End date (inclusive).</param>
    /// <returns>A <see cref="FeedSeries"/> if any data was found; otherwise <c>null</c>.</returns>
    public FeedSeries? Load(
        string dataRoot,
        string exchange,
        string assetDir,
        string feedName,
        string interval,
        DateOnly from,
        DateOnly to)
    {
        var timestamps = new List<long>();
        List<double>[]? columnLists = null;
        int missingColumnRows = 0;

        var current = new DateOnly(from.Year, from.Month, 1);
        var endMonth = new DateOnly(to.Year, to.Month, 1);

        var fromMs = new DateTimeOffset(from.Year, from.Month, from.Day, 0, 0, 0, TimeSpan.Zero)
            .ToUnixTimeMilliseconds();
        var toMs = new DateTimeOffset(to.Year, to.Month, to.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(1).ToUnixTimeMilliseconds() - 1;

        while (current <= endMonth)
        {
            var filePath = GetPartitionPath(dataRoot, exchange, assetDir, feedName, current, interval);
            if (!File.Exists(filePath))
            {
                current = current.AddMonths(1);
                continue;
            }

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(fs);

            var firstLine = true;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (firstLine)
                {
                    firstLine = false;
                    // Parse column count from header (skip "ts" column)
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

                // Ensure columnLists is initialised (handles files with no data before header)
                if (columnLists is null)
                {
                    var colCount = parts.Length - 1;
                    columnLists = new List<double>[colCount];
                    for (var i = 0; i < colCount; i++)
                        columnLists[i] = [];
                }

                // Parse all column values, skipping the row if any value is malformed
                var values = new double[columnLists.Length];
                bool parseFailed = false;
                for (var c = 0; c < columnLists.Length; c++)
                {
                    var valueIdx = c + 1;
                    if (valueIdx < parts.Length
                        && double.TryParse(parts[valueIdx], CultureInfo.InvariantCulture, out var v))
                    {
                        values[c] = v;
                    }
                    else if (valueIdx >= parts.Length)
                    {
                        values[c] = 0d;
                        missingColumnRows++;
                    }
                    else
                    {
                        parseFailed = true;
                        break;
                    }
                }

                if (parseFailed)
                    continue;

                timestamps.Add(ts);
                for (var c = 0; c < columnLists.Length; c++)
                    columnLists[c].Add(values[c]);
            }

            current = current.AddMonths(1);
        }

        if (missingColumnRows > 0)
        {
            _logger.LogWarning(
                "{Count} rows had fewer columns than header in {Feed}/{AssetDir} — filled with 0",
                missingColumnRows, feedName, assetDir);
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
