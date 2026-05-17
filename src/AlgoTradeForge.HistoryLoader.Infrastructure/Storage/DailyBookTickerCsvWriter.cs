using System.Globalization;
using AlgoTradeForge.Application.IO;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// Writes Binance best bid/ask snapshots to daily-partitioned CSVs at
/// <c>{assetDir}/book-ticker/&lt;YYYY-MM-DD&gt;.csv</c> with schema
/// <c>ts,bid_price,bid_qty,ask_price,ask_qty,update_id</c>. Dedup by <c>update_id</c> (Values[4]).
/// </summary>
internal sealed class DailyBookTickerCsvWriter : BufferedPartitionWriter, IBookTickerWriter
{
    private const int ValueCount = 5; // [bid_price, bid_qty, ask_price, ask_qty, update_id]
    private const string BookTickerHeader = "ts,bid_price,bid_qty,ask_price,ask_qty,update_id";

    public DailyBookTickerCsvWriter(
        IFileStorage storage,
        IPartitionTailIndex tailIndex,
        IOptions<HistoryLoaderStorageOptions> options,
        ILogger<DailyBookTickerCsvWriter> logger,
        WriteLockManager locks)
        : base(storage, tailIndex, options, logger, locks)
    {
    }

    public void Write(string assetDir, FeedRecord record)
    {
        if (record.Values.Length != ValueCount)
            throw new ArgumentException(
                $"BookTicker FeedRecord must have {ValueCount} values [bid_price, bid_qty, ask_price, ask_qty, update_id]; got {record.Values.Length}.",
                nameof(record));

        long updateId = (long)record.Values[4];
        var partitionKey = GetPartitionKey(assetDir, record.TimestampMs);

        var row =
            $"{record.TimestampMs.ToString(CultureInfo.InvariantCulture)}," +
            $"{record.Values[0].ToString(CultureInfo.InvariantCulture)}," +
            $"{record.Values[1].ToString(CultureInfo.InvariantCulture)}," +
            $"{record.Values[2].ToString(CultureInfo.InvariantCulture)}," +
            $"{record.Values[3].ToString(CultureInfo.InvariantCulture)}," +
            $"{updateId.ToString(CultureInfo.InvariantCulture)}";

        AppendRow(partitionKey, BookTickerHeader, row, updateId);
    }

    public async Task<BookTickerResumeState?> ResumeFrom(string assetDir, CancellationToken ct = default)
    {
        var feedDir = Path.Combine(assetDir, FeedNames.BookTicker);
        if (!Directory.Exists(feedDir)) return null;

        var files = Directory.GetFiles(feedDir, "????-??-??.csv")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0) return null;

        var latestFile = files[0];
        var lastLine = await TailIndex.GetLastLine(latestFile, ct);
        if (lastLine is null) return null;
        if (lastLine.StartsWith("ts,", StringComparison.Ordinal)
            || lastLine.Equals("ts", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = lastLine.Split(',');
        if (parts.Length != 6
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)
            || !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var updateId))
            return null;

        RegisterPartitionWatermark(latestFile, BookTickerHeader, updateId);
        return new BookTickerResumeState(updateId, ts);
    }

    private static string GetPartitionKey(string assetDir, long timestampMs)
    {
        var dayKey = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(assetDir, FeedNames.BookTicker, $"{dayKey}.csv");
    }
}
