using System.Globalization;
using AlgoTradeForge.Storage;
using AlgoTradeForge.HistoryLoader.Application;
using AlgoTradeForge.HistoryLoader.Application.Abstractions;
using AlgoTradeForge.HistoryLoader.Domain;
using AlgoTradeForge.HistoryLoader.Infrastructure.Storage.Buffered;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AlgoTradeForge.HistoryLoader.Infrastructure.Storage;

/// <summary>
/// Writes Binance aggregate trades to daily-partitioned CSVs at
/// <c>{assetDir}/ticks/&lt;YYYY-MM-DD&gt;.csv</c> with schema
/// <c>ts,price,qty,is_buyer_maker,agg_id</c>. Dedup by <c>agg_id</c> (Values[3]).
/// </summary>
/// <remarks>
/// Buffer-then-PUT: rows accumulate in memory and flush atomically via <see cref="IFileStorage"/>.
/// Torn-row recovery from the pre-PR3 implementation is gone — atomic publish (<c>.tmp + Move</c>
/// on local FS, single <c>PutObject</c> on S3) makes partial-row writes structurally impossible.
/// </remarks>
internal sealed class DailyTickCsvWriter : BufferedPartitionWriter, ITickFeedWriter
{
    private const int TickValueCount = 4; // [price, qty, is_buyer_maker, agg_id]
    private const string TickHeader = "ts,price,qty,is_buyer_maker,agg_id";

    public DailyTickCsvWriter(
        IFileStorage storage,
        IPartitionTailIndex tailIndex,
        IOptions<HistoryLoaderStorageOptions> options,
        ILogger<DailyTickCsvWriter> logger,
        WriteLockManager locks)
        : base(storage, tailIndex, options, logger, locks)
    {
    }

    public void Write(string assetDir, FeedRecord record)
    {
        if (record.Values.Length != TickValueCount)
            throw new ArgumentException(
                $"Tick FeedRecord must have {TickValueCount} values [price, qty, is_buyer_maker, agg_id]; got {record.Values.Length}.",
                nameof(record));

        // Binance agg_ids are monotonic per-symbol; the base dedup compares against the last seen.
        long aggId = (long)record.Values[3];
        var partitionKey = GetPartitionKey(assetDir, record.TimestampMs);

        var row =
            $"{record.TimestampMs.ToString(CultureInfo.InvariantCulture)}," +
            $"{record.Values[0].ToString(CultureInfo.InvariantCulture)}," +
            $"{record.Values[1].ToString(CultureInfo.InvariantCulture)}," +
            $"{((int)record.Values[2]).ToString(CultureInfo.InvariantCulture)}," +
            $"{aggId.ToString(CultureInfo.InvariantCulture)}";

        AppendRow(partitionKey, TickHeader, row, aggId);
    }

    public async Task<TickResumeState?> ResumeFrom(string assetDir, CancellationToken ct = default)
    {
        var feedDir = Path.Combine(assetDir, FeedNames.Ticks);
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
        if (parts.Length != 5
            || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)
            || !long.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var aggId))
            return null;

        RegisterPartitionWatermark(latestFile, TickHeader, aggId);
        return new TickResumeState(aggId, ts);
    }

    private static string GetPartitionKey(string assetDir, long timestampMs)
    {
        var dayKey = DateTimeOffset.FromUnixTimeMilliseconds(timestampMs)
            .UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Path.Combine(assetDir, FeedNames.Ticks, $"{dayKey}.csv");
    }
}
